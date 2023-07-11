using System.Linq;
using System.Security.Principal;
using HRMIS.BLL;
using HRMIS.Logging;
using HRMIS.POCO;
using HRMIS.PayrollLibrary;
using ikubINFO.Providers.WebSecurity;
using HRMIS.Helpers;

namespace HRMIS.BusinessServices
{
    public class HREmployePayrollManager : IEmployeePayrollManager<EmployePayroll, EmployeeEnrollment>
    {
        private readonly RepositoryEmployeePayroll<EmployePayroll, EmployeeEnrollment> repository;
        private readonly UserContext ucntx;
        private readonly IPayrollPeriodManager<PayrollPeriod, SalaryPeriod, SalaryInstitutionPeriod> periodManager;
        private IHolyDayManager<Holiday> holidayManager { get; set; }
        private readonly IGeneralParameterManager<GeneralParameter> parameters;
        private readonly IPaymentElementManager<PayElement, PayElementContext> payElManager;
        private readonly IInstitutionPayrollManager<InstitutionPayroll, EmployePayroll> instPayrollMg;
        private readonly Dictionary<string, decimal> _cache = new Dictionary<string, decimal>();
        private readonly Dictionary<string, decimal> _salaryFactors = new Dictionary<string, decimal>();
        private bool hasError = false;
        private readonly List<string> stepProcessing = new List<string>();
        private readonly List<EmployeePayrollElement> payrollElementsCalculated = new List<EmployeePayrollElement>();
        private decimal dayCost;
        private decimal hourCost;
        private decimal hoursOnLeave = 0;
        private decimal totalInsuredAmount; // the amount based on which the insurance is calculated;
        private decimal totalSalaryMonth; // this is monthly salary + supplements
        private decimal totalTaxedAmount; // the amount based on which the tax will be calculated;
        private decimal grossTotal;
        private decimal netTotal;

        private decimal socialinsEmployee;
        private decimal socialinsEmployer;
        private decimal socialinsSalary;
        private decimal taxSalary;
        private decimal taxTotal;
        private decimal addInsTotal;

        private decimal healthInsEmployee;
        private decimal healthInsEmployer;
        private decimal healthInsSalary;

        private decimal dismissWorkHours = 0;
        private decimal totWorkingHoursPerMonth = 0;
        private decimal workingDaysForSocialContribution = 0;
        private int totRaportDays = 0;
        private decimal leavesSalary = 0;
        private decimal grossleaveSalary = 0;
        private int employWorkHoursAsPerContract = 0;
        private bool reduceRaport = false;
        private bool reduceAbsentDays = false;
        private int posCount = 0;





        public HREmployePayrollManager(RepositoryEmployeePayroll<EmployePayroll, EmployeeEnrollment> rep,
            IInstitutionPayrollManager<InstitutionPayroll, EmployePayroll> intpayroll,
            IPayrollPeriodManager<PayrollPeriod, SalaryPeriod, SalaryInstitutionPeriod> periodMg,
            IGeneralParameterManager<GeneralParameter> pars,
            IPaymentElementManager<PayElement, PayElementContext> elementMg,
            IPrincipal principal)
        {
            if (rep == null)
                throw new ArgumentException(ErrorMessages.The_Employe_Payroll_Manager_needs_a_repository_object_which_it_was_not_provided);
            repository = rep;
            if (periodMg == null)
                throw new ArgumentException(ErrorMessages.The_Employe_Payroll_Manager_needs_a_salary_period_manager_which_was_not_provided);
            periodManager = periodMg;

            if (intpayroll == null)
            {
                throw new ArgumentException(ErrorMessages.The_EmployeePayroll_Manager_Needs_An_institution_payrollmanager);
            }
            this.instPayrollMg = intpayroll;

            if (pars == null)
                throw new ArgumentException(ErrorMessages.The_Employe_Payroll_Manager_needs_a_general_parameter_manager_which_was_not_provided);
            parameters = pars;

            if (elementMg == null)
                throw new ArgumentException(ErrorMessages.The_Employe_Payroll_Manager_needs_a_payment_elements_manager_which_was_not_provided);
            payElManager = elementMg;

            if (principal == null)
                throw new ArgumentException(ErrorMessages.The__Employe_Payroll_Manager_needs_a_security_principal_object_which_it_was_not_provided);
            ucntx = principal as UserContext;
            if (ucntx == null)
                throw new ArgumentException(ErrorMessages.The_security_principal_object_is_not_supported);
        }


        public Result<EmployePayroll> CalculatePayroll(EmployePayroll payroll)
        {
            Result<EmployePayroll> res = null;
            try
            {
                this.hasError = false;
                this.stepProcessing.Clear();
                if (payroll.EmployeeEnrollment == null)
                {
                    res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Invalid_work_position);
                    return res;
                }
                else
                {

                    if (payroll.EmployeeEnrollment.EmployeeEnrolled == null)
                    { res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Employee_not_specified); return res; }
                    if (payroll.EmployeeEnrollment.Position == null)
                    { res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Work_position_not_specified); return res; }
                    if (string.IsNullOrEmpty(payroll.InstitutionID))
                    { res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Invalid_institution_ID); return res; }
                    if (string.IsNullOrEmpty(payroll.EmployeeEnrollment.EmployeeEnrolled.BankAccount))
                    { res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Employee_BankAccount_Not_Defined); return res; }
                    if (payroll.EmployeeEnrollment.EmployeeEnrolled.MyBank == null)
                    { res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Employee_BankAccount_Not_Defined); return res; }

                    Result<SalaryInstitutionPeriod> pRes = this.periodManager.GetActiveInstitutionPeriod(payroll.InstitutionID);
                    if (pRes.HasError) { res = new Result<EmployePayroll>(null, pRes.HasError, pRes.MessageResult); return res; }
                    SalaryInstitutionPeriod period = pRes.ReturnValue;

                    if (!CanPayrollBeCalculated(payroll, period))
                    {
                        res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Cannot_Calculate_Payroll_Per_Period_Invalid_Assigment); return res;
                    }
                    var myPayrollListRes = instPayrollMg.GetInstitutionPayroll(payroll.InstitutionID, period.Key);
                    if (myPayrollListRes.HasError)
                    {
                        res = new Result<EmployePayroll>(payroll, true, myPayrollListRes.MessageResult); return res;
                    }

                    if (myPayrollListRes.ReturnValue.Approved)
                    {
                        res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Employe_Payroll_is_approved_and_can_not_be_updated__); return res;
                    }

                    EmployePayroll myPayroll = repository.GetPayrollMainData(payroll.EmployeeEnrollment, period.Key);

                    if (myPayroll != null)
                    {
                        if (myPayroll.IsApproved) { res = new Result<EmployePayroll>(myPayroll, true, ErrorMessages.Employe_Payroll_is_approved_and_can_not_be_updated__); return res; }
                        payroll = myPayroll;
                    }

                    payroll.MyPayrollList = myPayrollListRes.ReturnValue;
                    payroll.MyPayrollList.Period = period;
                    payroll.MyBank = payroll.EmployeeEnrollment.EmployeeEnrolled.MyBank;
                    payroll.BankAccount = payroll.EmployeeEnrollment.EmployeeEnrolled.BankAccount;

                    // general parameters give info about how many working days there are in a month and how many working hours there are in a day
                    Result<GeneralParameter> gRes = this.parameters.GetActiveGeneralParameters();
                    if (gRes.HasError) { res = new Result<EmployePayroll>(null, gRes.HasError, gRes.MessageResult); return res; }
                    GeneralParameter param = gRes.ReturnValue;

                    // make all the salary components 0
                    ClearState();

                    // clear all the related elements for this employee like Suplements, Deductions, Leaves, Overtime, Working days ...
                    GetPayrollRelatedElements(payroll.EmployeeEnrollment, period.Key);

                    totWorkingHoursPerMonth = CalculateWorkingDaysHours(param, payroll, period);


                    decimal d1 = CalculateBaseSalary(payroll, param);
                    decimal d2 = CalculateBasedOnInstitution(payroll, param);
                    decimal d3 = CalculateBasedOnStructure(payroll, param);
                    decimal d4 = CalculateBasedOnGroup(payroll, param);
                    decimal d5 = CalculatedEmployeeSupplements(payroll, param);


                    totalSalaryMonth +=
                        (d1 + d2 + d3 + d4 + d5);

                    decimal h = CalculateHoldOnLeaves(param, payroll);
                    totalSalaryMonth += h;

                    _cache.Add("G", totalSalaryMonth);
                    _salaryFactors.Add("G", totalSalaryMonth);

                    FillPayrollWithCalculatedFields(ref payroll);


                    if (hasError) { res = new Result<EmployePayroll>(null, true, ErrorMessages.Error_in_calculating_payroll); return res; }

                    foreach (EmployeePayrollElement el in payrollElementsCalculated)
                    {
                        if (el.PayElementVersion == -1)
                            el.MyPayElement = null;
                        el.EvaluationConfig = this.SerializePayElementJSON(el);
                        el.CreatedBy = ucntx.UserID;
                        el.CreatedOn = DateTime.Now;
                        el.CreatedIP = ucntx.IP;
                        el.ModifiedBy = ucntx.UserID;
                        el.ModifiedOn = el.CreatedOn;
                        el.ModifiedIP = ucntx.IP;

                    }

                    payroll.PayElements = new ReadOnlyCollection<EmployeePayrollElement>(payrollElementsCalculated);
                    payroll.InsuredLeaveDays = totRaportDays;
                    res = PersistPayroll(payroll);
                }
            }
            catch (Exception exp)
            {
                res = new Result<EmployePayroll>(null, true, exp);
                Logger.Error(exp);
            }

            return res;
        }

        public Result<EmployePayroll> CalculateDetailedPayroll(EmployePayroll payroll, EmployePayroll previousPayroll)
        {
            Result<EmployePayroll> res = null;
            try
            {


                this.hasError = false;
                this.stepProcessing.Clear();
                if (payroll.EmployeeEnrollment == null)
                {
                    res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Invalid_work_position);
                    return res;
                }
                else
                {

                    if (payroll.EmployeeEnrollment.EmployeeEnrolled == null)
                    { res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Employee_not_specified); return res; }
                    if (payroll.EmployeeEnrollment.Position == null)
                    { res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Work_position_not_specified); return res; }
                    if (string.IsNullOrEmpty(payroll.InstitutionID))
                    { res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Invalid_institution_ID); return res; }
                    if (string.IsNullOrEmpty(payroll.EmployeeEnrollment.EmployeeEnrolled.BankAccount))
                    { res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Employee_BankAccount_Not_Defined); return res; }
                    if (payroll.EmployeeEnrollment.EmployeeEnrolled.MyBank == null)
                    { res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Employee_BankAccount_Not_Defined); return res; }

                    Result<SalaryInstitutionPeriod> pRes = this.periodManager.GetActiveInstitutionPeriod(payroll.InstitutionID);
                    if (pRes.HasError) { res = new Result<EmployePayroll>(null, pRes.HasError, pRes.MessageResult); return res; }
                    SalaryInstitutionPeriod period = pRes.ReturnValue;

                    if (!this.CanPayrollBeCalculated(payroll, period))
                    {
                        res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Cannot_Calculate_Payroll_Per_Period_Invalid_Assigment); return res;
                    }
                    var myPayrollListRes = instPayrollMg.GetInstitutionPayroll(payroll.InstitutionID, period.Key);
                    if (myPayrollListRes.HasError)
                    {
                        res = new Result<EmployePayroll>(payroll, true, myPayrollListRes.MessageResult); return res;
                    }

                    if (myPayrollListRes.ReturnValue.Approved)
                    {
                        res = new Result<EmployePayroll>(payroll, true, ErrorMessages.Employe_Payroll_is_approved_and_can_not_be_updated__); return res;
                    }

                    EmployePayroll myPayroll = repository.GetPayrollMainData(payroll.EmployeeEnrollment, period.Key);

                    if (myPayroll != null)
                    {
                        if (myPayroll.IsApproved) { res = new Result<EmployePayroll>(myPayroll, true, ErrorMessages.Employe_Payroll_is_approved_and_can_not_be_updated__); return res; }
                        payroll = myPayroll;
                    }

                    payroll.MyPayrollList = myPayrollListRes.ReturnValue;
                    payroll.MyPayrollList.Period = period;
                    payroll.MyBank = payroll.EmployeeEnrollment.EmployeeEnrolled.MyBank;
                    payroll.BankAccount = payroll.EmployeeEnrollment.EmployeeEnrolled.BankAccount;

                    Result<GeneralParameter> gRes = this.parameters.GetActiveGeneralParameters();
                    if (gRes.HasError) { res = new Result<EmployePayroll>(null, gRes.HasError, gRes.MessageResult); return res; }
                    GeneralParameter param = gRes.ReturnValue;

                    posCount = previousPayroll != null ? (previousPayroll.EmployeSSN.Equals(payroll.EmployeSSN) ? 2 : 1) : 1;

                    // ketu fillon llogaritja
                    ClearState();

                    GetPayrollRelatedElements(payroll.EmployeeEnrollment, period.Key);

                    totWorkingHoursPerMonth = CalculateWorkingDaysHours(param, payroll, period);

                    decimal d1 = CalculateBaseSalary(payroll, param);
                    decimal d2 = CalculateBasedOnInstitution(payroll, param);
                    decimal d3 = CalculateBasedOnStructure(payroll, param);
                    decimal d4 = CalculateBasedOnGroup(payroll, param);
                    decimal d5 = CalculatedEmployeeSupplements(payroll, param);

                    totalSalaryMonth +=
                        (d1 + d2 + d3 + d4 + d5);
                    //  CalculateBaseSalary(payroll, param) 
                    //+ CalculateBasedOnInstitution(payroll, param) 
                    //+ CalculateBasedOnStructure(payroll, param)
                    //+ CalculateBasedOnGroup(payroll, param) 
                    //+ CalculatedEmployeeSupplements(payroll, param);

                    decimal h = CalculateHoldOnLeaves(param, payroll);
                    totalSalaryMonth += h;

                    FillPayrollWithCalculatedFields(ref payroll);


                    if (previousPayroll.EmployeSSN.Equals(payroll.EmployeSSN))
                    {
                        payroll = CalculateWithPreviousPayroll(previousPayroll, payroll);
                    }


                    if (hasError) { res = new Result<EmployePayroll>(null, true, ErrorMessages.Error_in_calculating_payroll); return res; }

                    foreach (EmployeePayrollElement el in payrollElementsCalculated)
                    {
                        el.EvaluationConfig = this.SerializePayElementJSON(el);
                        el.CreatedBy = ucntx.UserID;
                        el.CreatedOn = DateTime.Now;
                        el.CreatedIP = ucntx.IP;
                        el.ModifiedBy = ucntx.UserID;
                        el.ModifiedOn = el.CreatedOn;
                        el.ModifiedIP = ucntx.IP;
                        if (el.PayElementVersion == -1)
                            el.MyPayElement = null;
                    }

                    payroll.PayElements = new ReadOnlyCollection<EmployeePayrollElement>(payrollElementsCalculated);
                    payroll.InsuredLeaveDays = totRaportDays;
                    res = PersistPayroll(payroll);


                }
            }
            catch (Exception exp)
            {
                res = new Result<EmployePayroll>(null, true, exp);
                Logger.Error(exp);
            }

            return res;
        }


        public EmployePayroll CalculateWithPreviousPayroll(EmployePayroll previouspayroll, EmployePayroll payroll)
        {

            totalInsuredAmount += Math.Round(previouspayroll.ContribSalary, MidpointRounding.AwayFromZero);
            totalTaxedAmount += Math.Round(previouspayroll.TaxSalary, MidpointRounding.AwayFromZero);
            grossTotal += Math.Round(previouspayroll.GrossSalary, MidpointRounding.AwayFromZero);


            this._cache["C"] = totalInsuredAmount;
            this._salaryFactors["C"] = totalInsuredAmount;

            this._cache["T"] = totalTaxedAmount;
            this._salaryFactors["T"] = totalTaxedAmount;


            payroll.GrossSalary = Math.Round(grossTotal, MidpointRounding.AwayFromZero);
            payroll.TaxSalary = Math.Round(totalTaxedAmount, MidpointRounding.AwayFromZero);
            payroll.ContribSalary = Math.Round(totalInsuredAmount, MidpointRounding.AwayFromZero);

            CalculateSocialInsurance(payroll);
            CalculateHealthInsurance(payroll);
            CalculateTax(payroll);

            payroll.Deductions +=
                  Math.Round(previouspayroll.Deductions, MidpointRounding.AwayFromZero)
                + Math.Round(previouspayroll.AdditionalInsurance, MidpointRounding.AwayFromZero);

            payroll.NetSalary =
                  Math.Round(grossTotal, MidpointRounding.AwayFromZero)
                - Math.Round(socialinsEmployee, MidpointRounding.AwayFromZero)
                - Math.Round(healthInsEmployee, MidpointRounding.AwayFromZero)
                - Math.Round(addInsTotal, MidpointRounding.AwayFromZero)
                - Math.Round(taxTotal, MidpointRounding.AwayFromZero)
                - Math.Round(payroll.Deductions, MidpointRounding.AwayFromZero);

            return payroll;
        }


        public Result<EmployePayroll> UpdatePayroll(EmployePayroll payroll)
        {
            Result<EmployePayroll> res = null;
            try
            {
                if (string.IsNullOrEmpty(payroll.Key))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee_payroll_ID);
                }


                EmployePayroll myPayroll = repository.GetPayrollData(payroll.Key);

                if (myPayroll == null) return new Result<EmployePayroll>(myPayroll, true, ErrorMessages.A_payroll_does_not_exists);
                if (myPayroll.IsApproved) return new Result<EmployePayroll>(myPayroll, true, ErrorMessages.Employe_Payroll_is_approved_and_can_not_be_updated__);

                payroll.CreatedBy = ucntx.UserID;
                payroll.CreatedOn = DateTime.Now;
                payroll.CreatedIP = ucntx.IP;
                payroll.ModifiedBy = ucntx.UserID;
                payroll.ModifiedOn = payroll.CreatedOn;
                payroll.ModifiedIP = ucntx.IP;

                repository.UpdatePayroll(payroll);

                res = new Result<EmployePayroll>(payroll, false, ErrorMessages.Employe_Payroll_Succesfully_updated);
            }
            catch (Exception exp)
            {
                res = new Result<EmployePayroll>(null, true, exp);
                Logger.Error(exp);
            }

            return res;
        }

        public Result<EmployePayroll> GetPayroll(string key)
        {
            Result<EmployePayroll> res = null;
            try
            {
                if (string.IsNullOrEmpty(key))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee_payroll_ID);
                }


                EmployePayroll myPayroll = repository.GetPayrollData(key);

                if (myPayroll == null) return new Result<EmployePayroll>(myPayroll, true, ErrorMessages.A_payroll_does_not_exists);


                res = new Result<EmployePayroll>(myPayroll, false, ErrorMessages.Employe_Payroll_Succesfully_found);
            }
            catch (Exception exp)
            {
                res = new Result<EmployePayroll>(null, true, exp);
                Logger.Error(exp);
            }

            return res;
        }

        public Result<EmployePayroll> GetPayroll(EmployeeEnrollment enroll, string periodID, bool fullLoad)
        {
            Result<EmployePayroll> res = null;
            try
            {
                if (enroll == null)
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee_assigment_object);
                }
                if (enroll.Position == null)
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee_position_object);
                }
                if (enroll.EmployeeEnrolled == null)
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee__object);
                }
                if (string.IsNullOrEmpty(enroll.EmployeeEnrolled.Key))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee_assigment_object_key);
                }
                if (string.IsNullOrEmpty(enroll.Position.Key))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee_position_object_key);
                }
                if (string.IsNullOrEmpty(enroll.EmployeeEnrolled.Key))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee__object_key);
                }
                if (string.IsNullOrEmpty(periodID))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_period_ID);
                }


                EmployePayroll myPayroll = fullLoad ? repository.GetPayrollData(enroll, periodID) : repository.GetPayrollMainData(enroll, periodID);

                if (myPayroll == null) return new Result<EmployePayroll>(myPayroll, true, ErrorMessages.A_payroll_does_not_exists);


                res = new Result<EmployePayroll>(myPayroll, false, ErrorMessages.Employe_Payroll_Succesfully_found);
            }
            catch (Exception exp)
            {
                res = new Result<EmployePayroll>(null, true, exp);
                Logger.Error(exp);
            }

            return res;
        }
        public MultiResult<EmployePayroll> GetPayrollsForEmployeeID(EmployeeEnrollment enroll, string periodID)
        {
            MultiResult<EmployePayroll> res = null;
            try
            {
                if (enroll == null)
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee_assigment_object);
                }
                if (enroll.Position == null)
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee_position_object);
                }
                if (enroll.EmployeeEnrolled == null)
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee__object);
                }
                if (string.IsNullOrEmpty(enroll.EmployeeEnrolled.Key))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee_assigment_object_key);
                }
                if (string.IsNullOrEmpty(enroll.Position.Key))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee_position_object_key);
                }
                if (string.IsNullOrEmpty(enroll.EmployeeEnrolled.Key))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee__object_key);
                }
                if (string.IsNullOrEmpty(periodID))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_period_ID);
                }


                List<EmployePayroll> myPayroll = repository.GetPayrollsForEmployeeID(enroll, periodID);

                if (myPayroll == null) return new MultiResult<EmployePayroll>(myPayroll, true, ErrorMessages.A_payroll_does_not_exists);


                res = new MultiResult<EmployePayroll>(myPayroll, false, ErrorMessages.Employe_Payroll_Succesfully_found);
            }
            catch (Exception exp)
            {
                res = new MultiResult<EmployePayroll>(null, true, exp);
                Logger.Error(exp);
            }

            return res;
        }
        public MultiResult<EmployePayroll> GetAllPayrolls(string instID, string periodID)
        {
            MultiResult<EmployePayroll> res = null;
            try
            {
                if (string.IsNullOrEmpty(instID))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_institution_ID);
                }
                if (string.IsNullOrEmpty(periodID))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_period_ID);
                }

                List<EmployePayroll> myPayroll = repository.GetAllPayrollData(instID, periodID);

                if (myPayroll == null) return new MultiResult<EmployePayroll>(myPayroll, true, ErrorMessages.A_payroll_does_not_exists);


                res = new MultiResult<EmployePayroll>(myPayroll, false, ErrorMessages.Employe_Payroll_Succesfully_found);
            }
            catch (Exception exp)
            {
                res = new MultiResult<EmployePayroll>(null, true, exp);
                Logger.Error(exp);
            }

            return res;
        }
       
        public Result<EmployeeEnrollment> GetEmployeeLeaves(EmployeeEnrollment enroll, string periodID)
        {
            Result<EmployeeEnrollment> res = null;
            try
            {
                if (string.IsNullOrEmpty(enroll.Key))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee_enrollment_ID);
                }

                repository.GetEmployeLeaves(enroll,periodID);
                res = new Result<EmployeeEnrollment>(enroll, false, ErrorMessages.Employe_Payroll_Elements_Succesfully_updated);
            }

            catch (Exception exp)
            {
                res = new Result<EmployeeEnrollment>(null, true, exp);
                Logger.Error(exp);
            }

            return res;
        }
    
            public Result<EmployeeEnrollment> UpdatePayrollRelatedElements(EmployeeEnrollment enroll, string periodID)
        {
            Result<EmployeeEnrollment> res = null;
            try
            {
                if (string.IsNullOrEmpty(enroll.Key))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee_enrollment_ID);
                }

                foreach (EmployeWorkDay d in enroll.MyWorkingDays)
                {
                    if (int.Parse(d.Key) < 0)
                    {
                        d.CreatedBy = ucntx.UserID;
                        d.CreatedOn = DateTime.Now;
                        d.CreatedIP = ucntx.IP;
                        d.ModifiedBy = ucntx.UserID;
                        d.ModifiedOn = d.CreatedOn;
                        d.ModifiedIP = ucntx.IP;
                    }
                    else
                    {
                        d.ModifiedBy = ucntx.UserID;
                        d.ModifiedOn = DateTime.Now;
                        d.ModifiedIP = ucntx.IP;
                    }
                }

                foreach (EmployeLeave d in enroll.Myleaves)
                {
                    if (int.Parse(d.Key) < 0)
                    {
                        d.CreatedBy = ucntx.UserID;
                        d.CreatedOn = DateTime.Now;
                        d.CreatedIP = ucntx.IP;
                        d.ModifiedBy = ucntx.UserID;
                        d.ModifiedOn = d.CreatedOn;
                        d.ModifiedIP = ucntx.IP;
                    }
                    else
                    {
                        d.ModifiedBy = ucntx.UserID;
                        d.ModifiedOn = DateTime.Now;
                        d.ModifiedIP = ucntx.IP;
                    }
                }

                foreach (EmployeOvertime d in enroll.MyOvertime)
                {
                    if (int.Parse(d.Key) < 0)
                    {
                        d.CreatedBy = ucntx.UserID;
                        d.CreatedOn = DateTime.Now;
                        d.CreatedIP = ucntx.IP;
                        d.ModifiedBy = ucntx.UserID;
                        d.ModifiedOn = d.CreatedOn;
                        d.ModifiedIP = ucntx.IP;
                    }
                    else
                    {
                        d.ModifiedBy = ucntx.UserID;
                        d.ModifiedOn = DateTime.Now;
                        d.ModifiedIP = ucntx.IP;
                    }
                }
                foreach (EmployePayElement d in enroll.MySupplements)
                {
                    if (int.Parse(d.Key) < 0)
                    {
                        d.CreatedBy = ucntx.UserID;
                        d.CreatedOn = DateTime.Now;
                        d.CreatedIP = ucntx.IP;
                        d.ModifiedBy = ucntx.UserID;
                        d.ModifiedOn = d.CreatedOn;
                        d.ModifiedIP = ucntx.IP;
                    }
                    else
                    {
                        d.ModifiedBy = ucntx.UserID;
                        d.ModifiedOn = DateTime.Now;
                        d.ModifiedIP = ucntx.IP;
                    }
                }

                foreach (EmployePayElement d in enroll.MyDeductions)
                {
                    if (int.Parse(d.Key) < 0)
                    {
                        d.CreatedBy = ucntx.UserID;
                        d.CreatedOn = DateTime.Now;
                        d.CreatedIP = ucntx.IP;
                        d.ModifiedBy = ucntx.UserID;
                        d.ModifiedOn = d.CreatedOn;
                        d.ModifiedIP = ucntx.IP;
                    }
                    else
                    {
                        d.ModifiedBy = ucntx.UserID;
                        d.ModifiedOn = DateTime.Now;
                        d.ModifiedIP = ucntx.IP;
                    }
                }

                repository.UpdateEmployeOvertimes(enroll, periodID);
                repository.UpdateEmployeLeaves(enroll, periodID);
                repository.UpdateEmployeWorkingDays(enroll, periodID);
                repository.UpdateEmployeSupplements(enroll, periodID);
                repository.UpdateEmployeDeductions(enroll, periodID);


                res = new Result<EmployeeEnrollment>(enroll, false, ErrorMessages.Employe_Payroll_Elements_Succesfully_updated);
            }
            catch (Exception exp)
            {
                res = new Result<EmployeeEnrollment>(null, true, exp);
                Logger.Error(exp);
            }

            return res;
        }

        public Result<EmployeeEnrollment> GetPayrollRelatedElements(EmployeeEnrollment enroll, string periodID)
        {
            Result<EmployeeEnrollment> res = null;
            try
            {
                if (string.IsNullOrEmpty(enroll.Key))
                {
                    throw new ApplicationException(ErrorMessages.Invalid_employee_enrollment_ID);
                }

                if (enroll.MySupplements != null && enroll.MySupplements.Count > 0) { enroll.MySupplements.Clear(); }
                if (enroll.MyDeductions != null && enroll.MyDeductions.Count > 0) { enroll.MyDeductions.Clear(); }
                if (enroll.Myleaves != null && enroll.Myleaves.Count > 0) { enroll.Myleaves.Clear(); }
                if (enroll.MyOvertime != null && enroll.MyOvertime.Count > 0) { enroll.MyOvertime.Clear(); }
                if (enroll.MyWorkingDays != null && enroll.MyWorkingDays.Count > 0) { enroll.MyWorkingDays.Clear(); }
                var result = repository.GetEmployePayElements(enroll, periodID);


                res = new Result<EmployeeEnrollment>(result, false, ErrorMessages.Employe_Pay_Elements_Succesfully_found);
            }
            catch (Exception exp)
            {
                res = new Result<EmployeeEnrollment>(null, true, exp);
                Logger.Error(exp);
            }

            return res;

        }

        //public Result<EmployeeEnrollment> GetAllPayrollRelatedElements(EmployeeEnrollment enroll)
        //{
        //    Result<EmployeeEnrollment> res = null;
        //    try
        //    {
        //        if (string.IsNullOrEmpty(enroll.Key))
        //        {
        //            throw new ApplicationException(ErrorMessages.Invalid_employee_enrollment_ID);
        //        }

             
        //        if (enroll.MyDeductions != null && enroll.MyDeductions.Count > 0) { enroll.MyDeductions.Clear(); }
              
        //        var result = repository.GetAllEmployePayElements(enroll);


        //        res = new Result<EmployeeEnrollment>(result, false, ErrorMessages.Employe_Pay_Elements_Succesfully_found);
        //    }
        //    catch (Exception exp)
        //    {
        //        res = new Result<EmployeeEnrollment>(null, true, exp);
        //        Logger.Error(exp);
        //    }

        //    return res;

        //}



        #region Private Calculate Payroll
        private void ClearState()
        {
            payrollElementsCalculated.Clear();
            _cache.Clear();
            _salaryFactors.Clear();
            stepProcessing.Clear();
            totalSalaryMonth = 0;
            grossTotal = 0;
            totalTaxedAmount = 0;
            totalInsuredAmount = 0;
            netTotal = 0;
            socialinsEmployee = 0;
            socialinsEmployer = 0;
            healthInsEmployee = 0;
            healthInsEmployer = 0;
            addInsTotal = 0;
            taxTotal = 0;
            leavesSalary = 0;
            grossleaveSalary = 0;
            dayCost = 0;
            hourCost = 0;
            hoursOnLeave = 0;
            socialinsSalary = 0;
            taxSalary = 0;
            healthInsSalary = 0;
            dismissWorkHours = 0;
            totWorkingHoursPerMonth = 0;
            totRaportDays = 0;
            employWorkHoursAsPerContract = 0;


        }

        private bool CanPayrollBeCalculated(EmployePayroll payroll, SalaryInstitutionPeriod period)
        {
            DateTime startPeriod = new DateTime(period.Period.Period.PeriodYear, period.Period.PeriodMonth, 1);
            DateTime endPeriod = startPeriod.AddMonths(1).AddDays(-1);
            bool canCalc = false;
            if (payroll.EmployeeEnrollment.StartFrom <= endPeriod)
            {
                DateTime endEmployement = payroll.EmployeeEnrollment.EndTo.HasValue ? payroll.EmployeeEnrollment.EndTo.Value : DateTime.MaxValue;

                if (endEmployement >= startPeriod)
                {
                    canCalc = true;
                }
            }

            return canCalc;
        }

        private decimal CalculateBaseSalary(EmployePayroll payroll, GeneralParameter param)
        {
            decimal _baseSalary = 0;
            decimal baseMonthlySalary = 0;
            string proccessing = "1) Calculating base salary: ";

            MultiResult<PayElementContext> _resPayCntx1 =
                this.payElManager.GetPayelementContextPayGrade(payroll.EmployeeEnrollment.Position.PayGradeID);

            MultiResult<PayElementContext> _resPayCntx2 =
                this.payElManager.GetPayelementContext(payroll.InstitutionID, payroll.EmployeeEnrollment.Position.OrgID, payroll.EmployeeEnrollment.Position.OrgGrpID);

            if (_resPayCntx1.HasError)
            {
                this.hasError = true; proccessing += "[There was an error finding context salary element per pay grade]";
                throw new ApplicationException(proccessing + _resPayCntx1.MessageResult);
            }

            if (_resPayCntx2.HasError)
            {
                this.hasError = true; proccessing += "[There was an error finding context salary element per organogram]";
                throw new ApplicationException(proccessing + _resPayCntx2.MessageResult);
            }

            if (_resPayCntx1.ReturnValue == null || _resPayCntx1.ReturnValue.Count <= 0)
            {
                this.hasError = true; proccessing += "[No context salary element found per pay grade]";
                throw new ApplicationException(proccessing);
            }

            PayElementContext _el =
                _resPayCntx1.ReturnValue.Find(e => ((e.MyElement.ElementType.Key == "1") && (e.MyElement.Active == true)));

            if (_el == null)
            {
                this.hasError = true; proccessing += "[No context base salary element found per pay grade]";
                throw new ApplicationException(proccessing);
            }

            if (_resPayCntx2.ReturnValue != null && _resPayCntx2.ReturnValue.Count > 0)
            {
                PayElementContext _el1 = _resPayCntx2.ReturnValue.Find(e => (e.MyElement.ElementType.Key == "1") && (e.MyElement.Active == true));
                if (_el1 != null)
                {
                    _el = _el1;
                }
            }

            var exp = new Expression(_el, payroll.EmployeeEnrollment.EmployeeEnrolled.Key, payroll.EmployeeEnrollment.Position.Key, payroll.Context);

            try
            {
                var bEl = (decimal)exp.CaclulateExpression(_cache, this.payElManager);
                bEl = Math.Round(bEl, MidpointRounding.AwayFromZero);
                //

                if (_el.IsBasedOnContractedHours)
                {
                    bEl = CalculateValueBasedOnHours(bEl, param);
                }
                bEl = Math.Round(bEl, MidpointRounding.AwayFromZero);
                _cache.Add(_el.MyElement.Code, bEl);

                EmployeePayrollElement pElement = new EmployeePayrollElement();
                pElement.MyPayElement = _el;
                pElement.PayElementVersion = _el.Version;
                pElement.ElementDescription = _el.MyElement.ElementName;
                pElement.EcconomicalAcc = _el.MyElement.EconomicAcc;
                //If element is based on working days than calculate

                decimal value = _el.MyElement.IsBasedOnWorkingDays ? bEl * payroll.PaidWorkDays / param.DaysPerMonth : bEl;
                pElement.CalculatedValue = Math.Round(value, MidpointRounding.AwayFromZero);

                pElement.ContextBased = true;
                payrollElementsCalculated.Add(pElement);

                _baseSalary += Math.Round(bEl, MidpointRounding.AwayFromZero);

                baseMonthlySalary += pElement.CalculatedValue;

                if (_el.MyElement.IsIncludedInLeaves)
                {
                    decimal salaryLeaveElement = CalculateSalaryLeaveElement(payroll, bEl, pElement.CalculatedValue);
                    leavesSalary += Math.Round(salaryLeaveElement, MidpointRounding.AwayFromZero);

                }

                pElement.CalculatedValue = Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);

                foreach (PayElementContext _elcntx in _resPayCntx1.ReturnValue)
                {
                    if (int.Parse(_elcntx.MyElement.ElementType.Key) > 1 && int.Parse(_elcntx.MyElement.ElementType.Key) < 4)
                    {
                        if ((!_elcntx.Active) || (!_elcntx.MyElement.Active)) continue;
                        var expCntx = new Expression(_elcntx, payroll.EmployeeEnrollment.EmployeeEnrolled.Key, payroll.EmployeeEnrollment.Position.Key, payroll.Context);

                        decimal elVal = (decimal)expCntx.CaclulateExpression(_cache, payElManager);
                        if (_elcntx.IsBasedOnContractedHours)
                        {
                            elVal = CalculateValueBasedOnHours(elVal, param);
                        }
                        elVal = Math.Round(elVal, MidpointRounding.AwayFromZero);

                        _cache.Add(_elcntx.MyElement.Code, elVal);

                        pElement = new EmployeePayrollElement();
                        pElement.MyPayElement = _elcntx;
                        pElement.PayElementVersion = _elcntx.Version;

                        var calcValue = _elcntx.MyElement.IsBasedOnWorkingDays ? elVal * payroll.PaidWorkDays / param.DaysPerMonth : elVal;
                        pElement.CalculatedValue = Math.Round(calcValue, MidpointRounding.AwayFromZero);

                        pElement.ElementDescription = _elcntx.MyElement.ElementName;
                        pElement.EcconomicalAcc = _elcntx.MyElement.EconomicAcc;
                        pElement.ContextBased = true;
                        payrollElementsCalculated.Add(pElement);

                        _baseSalary += Math.Round(elVal, MidpointRounding.AwayFromZero);
                        baseMonthlySalary += pElement.CalculatedValue;

                        if (_elcntx.MyElement.IsIncludedInLeaves)
                        {
                            decimal salaryLeaveElement = CalculateSalaryLeaveElement(payroll, elVal, pElement.CalculatedValue);
                            leavesSalary += Math.Round(salaryLeaveElement, MidpointRounding.AwayFromZero);
                        }

                        pElement.CalculatedValue = Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);


                    }
                }

                foreach (PayElementContext _elcntx in _resPayCntx2.ReturnValue)
                {
                    if (int.Parse(_elcntx.MyElement.ElementType.Key) > 1 && int.Parse(_elcntx.MyElement.ElementType.Key) < 4)
                    {
                        if ((!_elcntx.Active) || (!_elcntx.MyElement.Active)) continue;
                        var expCntx = new Expression(_elcntx, payroll.EmployeeEnrollment.EmployeeEnrolled.Key, payroll.EmployeeEnrollment.Position.Key, payroll.Context);

                        var elVal = (decimal)expCntx.CaclulateExpression(_cache, this.payElManager);
                        if (_elcntx.IsBasedOnContractedHours)
                        {
                            elVal = CalculateValueBasedOnHours(elVal, param);
                        }

                        elVal = Math.Round(elVal, MidpointRounding.AwayFromZero);

                        _cache.Add(_elcntx.MyElement.Code, elVal);

                        pElement = new EmployeePayrollElement();
                        pElement.MyPayElement = _elcntx;
                        pElement.PayElementVersion = _elcntx.Version;

                        var val = _elcntx.MyElement.IsBasedOnWorkingDays ? elVal * payroll.PaidWorkDays / param.DaysPerMonth : elVal;

                        pElement.CalculatedValue = Math.Round(val, MidpointRounding.AwayFromZero);

                        pElement.ElementDescription = _elcntx.MyElement.ElementName;
                        pElement.EcconomicalAcc = _elcntx.MyElement.EconomicAcc;
                        pElement.ContextBased = true;
                        payrollElementsCalculated.Add(pElement);

                        _baseSalary += Math.Round(elVal, MidpointRounding.AwayFromZero);
                        baseMonthlySalary += pElement.CalculatedValue;

                        if (_elcntx.MyElement.IsIncludedInLeaves)
                        {
                            decimal salaryLeaveElement = CalculateSalaryLeaveElement(payroll, elVal, pElement.CalculatedValue);
                            leavesSalary += Math.Round(salaryLeaveElement, MidpointRounding.AwayFromZero);
                        }

                        pElement.CalculatedValue = Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    }
                }

                _cache.Add("b", Math.Round(_baseSalary, MidpointRounding.AwayFromZero));
                this._salaryFactors.Add("b", Math.Round(_baseSalary, MidpointRounding.AwayFromZero));


                // baseMonthlySalary = Math.Round((_baseSalary * payroll.WorkingDays / param.DaysPerMonth),MidpointRounding.AwayFromZero);

                _cache.Add("m", Math.Round(baseMonthlySalary, MidpointRounding.AwayFromZero));
                this._salaryFactors.Add("m", Math.Round(baseMonthlySalary, MidpointRounding.AwayFromZero));

                proccessing += ErrorMessages._Successfully_Finished_;
                stepProcessing.Add(proccessing);
            }
            catch (Exception e)
            {
                hasError = true;
                proccessing += ErrorMessages.Operation_Stopped;
                stepProcessing.Add(proccessing);
                throw e;
            }

            return baseMonthlySalary;
        }

        // adjust the value to reflect the fact that this employee may be contracted for less then 8 hours
        private decimal CalculateValueBasedOnHours(decimal vl, GeneralParameter param)
        {
            //decimal vDayCost = vl / param.DaysPerMonth;
            //decimal vHourCost = vDayCost / param.HoursPerDay;
            //return vHourCost * employWorkHoursAsPerContract * param.DaysPerMonth;

            // less readable but more precise (in signficance)
            return (vl * employWorkHoursAsPerContract) / param.HoursPerDay;
        }

        private decimal CalculateWorkingDaysHours(GeneralParameter param, EmployePayroll payroll, SalaryInstitutionPeriod period)
        {
            decimal NoWorkingHours = 0;
            decimal totalWorkingHours = 0;
            int availableWorkDaysInPeriod = 0;

            // usually 8 hours, but can be less, according to the contract 
            employWorkHoursAsPerContract = payroll.EmployeeEnrollment.IsContracted ?
                            ((payroll.EmployeeEnrollment.ContractedHours <= 0) ? param.HoursPerDay : payroll.EmployeeEnrollment.ContractedHours)
                                            : param.HoursPerDay;


            // count the hours that are not payable
            if (payroll.EmployeeEnrollment.MyWorkingDays.Count > 0)
            {

                foreach (EmployeWorkDay day in payroll.EmployeeEnrollment.MyWorkingDays)
                {
                    if (day.WorkDay.DayOfWeek != DayOfWeek.Saturday
                        && day.WorkDay.DayOfWeek != DayOfWeek.Sunday
                        && day.WorkDay >= payroll.EmployeeEnrollment.StartFrom
                        && !day.Payable)
                    {
                        NoWorkingHours += (employWorkHoursAsPerContract < day.WorkingHours) ? employWorkHoursAsPerContract : day.WorkingHours;
                    }
                }

            }

            // count the hours on leave that are not payable
            // add these hours to the total of hours that are not payable (NoWorkingHours)
            // count the hours on leave ( regardless if they are payable or not )
            if (payroll.EmployeeEnrollment.Myleaves.Count > 0)
            {

                foreach (EmployeLeave leave in payroll.EmployeeEnrollment.Myleaves)
                {
                    if (leave.LeaveDate >= payroll.EmployeeEnrollment.StartFrom)
                    {
                        if (!leave.LeaveType.Payable
                            && leave.LeaveDate.DayOfWeek != DayOfWeek.Saturday
                            && leave.LeaveDate.DayOfWeek != DayOfWeek.Sunday && leave.IsActive)
                        {
                            NoWorkingHours += employWorkHoursAsPerContract;
                        }

                        //TO ADD - take in consideration Saturdays and Sundays ?????????
                        hoursOnLeave += employWorkHoursAsPerContract;
                    }
                }
            }



            DateTime startPeriod = new DateTime(period.Period.Period.PeriodYear, period.Period.PeriodMonth, 1);
            DateTime endPeriod = startPeriod.AddMonths(1);

            //Find the total nr of avalilable work days per period
            availableWorkDaysInPeriod = this.FindNrWorkDaysPerPeriod(startPeriod, endPeriod);

            bool employmentStartsOrEndsWithInPeriod;
            // Find the toal nr of available work days for the employe in this period in this position
            int totalDaysEmployeeIsAllowedToWork = CalculateEmployeeWorkHours(
                param,
                startPeriod,
                endPeriod,
                payroll.EmployeeEnrollment.StartFrom,
                payroll.EmployeeEnrollment.EndTo,
                availableWorkDaysInPeriod,
                out employmentStartsOrEndsWithInPeriod);

            int holidayDaysInThisPeriod = GetNumberOfHolidayDaysInThisPeriod(payroll, startPeriod, endPeriod);
            //Find total nr of Sick leave
            CalculateInsuredNoWorkingDays(param, payroll);
            int totalUnPaidLeaveDays = Convert.ToInt32(NoWorkingHours / employWorkHoursAsPerContract);
            int totalPaidAndUnpaidLeaveDays = totalUnPaidLeaveDays + totRaportDays;

            var workDaysAndHolidays = availableWorkDaysInPeriod - totalPaidAndUnpaidLeaveDays;
            if (availableWorkDaysInPeriod <= totalPaidAndUnpaidLeaveDays || totalDaysEmployeeIsAllowedToWork <= totalPaidAndUnpaidLeaveDays)
            {
                payroll.PaidWorkDays = 0;
                payroll.UnpaidAbsentDays = totalUnPaidLeaveDays;
                payroll.PaidAbsentDays = totRaportDays;
            }
            else if (availableWorkDaysInPeriod <= param.DaysPerMonth) // Period has 22 days or less
            {
                if (employmentStartsOrEndsWithInPeriod)
                {
                    payroll.PaidWorkDays = totalDaysEmployeeIsAllowedToWork - totalPaidAndUnpaidLeaveDays;
                }
                else
                {
                    // Employee has been absent all month (paid or unpaid), yes, the UI allows this!
                    if (workDaysAndHolidays <= holidayDaysInThisPeriod)
                    {
                        payroll.PaidWorkDays = availableWorkDaysInPeriod - totalPaidAndUnpaidLeaveDays;
                    }
                    else
                    {
                        payroll.PaidWorkDays = param.DaysPerMonth - totalPaidAndUnpaidLeaveDays;
                    }
                }
                payroll.UnpaidAbsentDays = totalUnPaidLeaveDays;
                payroll.PaidAbsentDays = totRaportDays;
            }
            else // Period has more than 22 days
            {
                // Since period has more than 22 days, we need to remove 1 day from Unpaid Leaves (Absences) or Paid Leaves (Raports)
                if (totalUnPaidLeaveDays > 0 || totRaportDays > 0) // Paid or Unpaid Laves
                {
                    // Subtract -1 days if this is the second position, or the first position and the employee has word the whole month in this position
                    if (posCount > 1 || (posCount == 1 && totalDaysEmployeeIsAllowedToWork == availableWorkDaysInPeriod))
                    {
                        if (totalUnPaidLeaveDays > 0) // Priority Nr 1 -- Unpaid Leaves
                        {
                            payroll.UnpaidAbsentDays = totalUnPaidLeaveDays - 1;
                            payroll.PaidAbsentDays = totRaportDays;
                        }
                        else // (totRaportDays > 0)
                        {
                            payroll.UnpaidAbsentDays = totalUnPaidLeaveDays;
                            payroll.PaidAbsentDays = totRaportDays - 1;
                        }
                        if (posCount > 1)
                        {
                            payroll.PaidWorkDays = totalDaysEmployeeIsAllowedToWork - payroll.UnpaidAbsentDays - payroll.PaidAbsentDays;
                        }
                        else
                        {
                            payroll.PaidWorkDays = param.DaysPerMonth - payroll.UnpaidAbsentDays - payroll.PaidAbsentDays;
                        }
                    }
                    else
                    {
                        payroll.UnpaidAbsentDays = totalUnPaidLeaveDays;
                        payroll.PaidAbsentDays = totRaportDays;
                        payroll.PaidWorkDays = totalDaysEmployeeIsAllowedToWork - payroll.UnpaidAbsentDays - payroll.PaidAbsentDays;
                    }
                }
                else
                {
                    if (employmentStartsOrEndsWithInPeriod)
                    {
                        payroll.PaidWorkDays = totalDaysEmployeeIsAllowedToWork;
                    }
                    else
                    {
                        payroll.PaidWorkDays = param.DaysPerMonth;
                    }
                }
            }


            _cache.Add("w", payroll.PaidWorkDays);
            _salaryFactors.Add("w", payroll.PaidWorkDays);
            return payroll.PaidWorkDays * employWorkHoursAsPerContract;
        }

        private decimal CalculateHoldOnLeaves(GeneralParameter param, EmployePayroll payroll)
        {
            decimal holdOnLeave = 0;
            decimal totalLeaves = 0;
            decimal mamount = 0;

            //  var groupings = payroll.EmployeeEnrollment.Myleaves.GroupBy(l => l.LeaveType.Key);

            //var groupings = payroll.EmployeeEnrollment.Myleaves.GroupBy(l => l.IsActive);

            //Calculate 
            //foreach (var grp in groupings)
            //{
                var grp = payroll.EmployeeEnrollment.Myleaves.ToList();
                var first = grp.FirstOrDefault(e => e.LeaveType.IsSocialInsured);

                if (first != null)
                {
                    decimal nrD = first.LeaveType.MaxDays < payroll.PaidAbsentDays ? first.LeaveType.MaxDays : payroll.PaidAbsentDays;
                //decimal nrD = first.LeaveType.MaxDays < grp.Count(l => l.LeaveDate.DayOfWeek != DayOfWeek.Saturday &&
                //    l.LeaveDate.DayOfWeek != DayOfWeek.Sunday && l.IsActive) ? first.LeaveType.MaxDays : grp.Count(l => l.LeaveDate.DayOfWeek != DayOfWeek.Saturday &&
                //    l.LeaveDate.DayOfWeek != DayOfWeek.Sunday && l.IsActive);
                /*nrD = payroll.PaidAbsentDays;*/ // per change request of nr of work days. Refactor is needed for the whole method????
                holdOnLeave = payroll.PaidWorkDays == 0 ? nrD / param.DaysPerMonth * first.LeaveType.Percentage / 100 * leavesSalary
                        : (nrD / payroll.PaidWorkDays) * first.LeaveType.Percentage / 100 * leavesSalary;
                    EmployeePayrollElement pElement = new EmployeePayrollElement();

                    var value = Math.Round(holdOnLeave, MidpointRounding.AwayFromZero);
                    pElement.PayElementVersion = -1;//to define its a leave type
                    pElement.CalculatedValue = value;
                    pElement.ElementDescription = first.LeaveType.LeaveType;
                    pElement.EcconomicalAcc = first.LeaveType.EconomicAcc;
                    pElement.ContextBased = false;
                    pElement.MyPayElement = new PayElement();


                    payrollElementsCalculated.Add(pElement);

                    totalLeaves += value;
                //}
            }


            totalLeaves = Math.Round(totalLeaves, MidpointRounding.AwayFromZero);
            grossleaveSalary = totalLeaves;
            payroll.LeaveSalary = totalLeaves;
            if (_salaryFactors.TryGetValue("m", out mamount))
            {
                mamount = mamount + totalLeaves;
                this._cache.Remove("m");
                this._cache.Add("m", mamount);
                this._salaryFactors.Remove("m");
                this._salaryFactors.Add("m", mamount);

            }
            else
                throw new ApplicationException("Mungojne elementet perberes te llogaritjes se pages bruto mujore!!!");

            return totalLeaves;
        }

        /*  private void CalculateInsuredNoWorkingDays(GeneralParameter param,EmployePayroll payroll)
              {
              int nrDaysInsured = 0; 

                var groupings = payroll.EmployeeEnrollment.Myleaves.GroupBy(l => l.LeaveType.Key);

                foreach(var grp in groupings)
                  {

                  var first = 
                      grp.FirstOrDefault(e => e.LeaveType.IsSocialInsured == true);
                                              //&& e.LeaveDate.DayOfWeek != DayOfWeek.Saturday 
                                              //&& e.LeaveDate.DayOfWeek != DayOfWeek.Sunday);

                  if (first != null)
                      {

                       nrDaysInsured = grp.Count();

                      totRaportDays += nrDaysInsured;

                        if (nrDaysInsured>=param.DaysPerMonth)
                            {
                            payroll.NoWorkingDays = param.DaysPerMonth;
                            payroll.WorkingDays = 0;

                            }
                        else
                          if(nrDaysInsured>first.LeaveType.MaxDays)
                              {
                                payroll.NoWorkingDays = payroll.NoWorkingDays + nrDaysInsured; //- first.LeaveType.MaxDays;
                                payroll.WorkingDays =payroll.WorkingDays - nrDaysInsured ;
                              }
                        else
                      {
                          payroll.NoWorkingDays += nrDaysInsured;
                          payroll.WorkingDays = payroll.WorkingDays - nrDaysInsured;
                      }



                      }


                  }

              }
  */

        /// <summary>
        /// add/substract insured leaves from working/noworking days
        /// </summary>
        /// <param name="param"></param>
        /// <param name="payroll"></param>
        private void CalculateInsuredNoWorkingDays(GeneralParameter param, EmployePayroll payroll)
        {
            int nrDaysInsured = 0;

            // insured leaves
            var leaveGroups = payroll.EmployeeEnrollment.Myleaves
                .Where(l => l.LeaveType.IsSocialInsured && l.LeaveDate.DayOfWeek != DayOfWeek.Saturday && l.LeaveDate.DayOfWeek != DayOfWeek.Sunday && l.IsActive)
                .GroupBy(l => l.LeaveType.Key);

            foreach (var leaveGroup in leaveGroups)
            {

                var firstDayOfLeave = leaveGroup.FirstOrDefault();

                if (firstDayOfLeave != null)
                {
                    //exclude Saturday and Sunday
                    nrDaysInsured = leaveGroup.Count(l => l.LeaveDate.DayOfWeek != DayOfWeek.Saturday &&
                    l.LeaveDate.DayOfWeek != DayOfWeek.Sunday && l.IsActive);

                    totRaportDays += nrDaysInsured;

                    /* if (nrDaysInsured >= param.DaysPerMonth)
                     {
                         payroll.UnpaidAbsentDays = param.DaysPerMonth;
                         payroll.PaidWorkDays = 0;

                     }
                     else if (nrDaysInsured > firstDayOfLeave.LeaveType.MaxDays)
                     {
                         payroll.UnpaidAbsentDays = payroll.UnpaidAbsentDays + nrDaysInsured;
                         payroll.PaidWorkDays = payroll.PaidWorkDays - nrDaysInsured;
                     }
                     else
                     {
                         payroll.UnpaidAbsentDays += nrDaysInsured;
                         payroll.PaidWorkDays = payroll.PaidWorkDays - nrDaysInsured;
                     }*/

                }

            }

            /* if (totRaportDays >= param.DaysPerMonth)
             {
                 payroll.UnpaidAbsentDays = param.DaysPerMonth;
                 payroll.PaidWorkDays = 0;
             }
             else
                 if (payroll.UnpaidAbsentDays > 0 && payroll.PaidWorkDays <= param.DaysPerMonth && payroll.PaidWorkDays>0)
             {
                 //payroll.UnpaidAbsentDays += totRaportDays;
                 payroll.PaidWorkDays = payroll.PaidWorkDays - totRaportDays;
             }
             else
             if (payroll.PaidWorkDays > 0)
             {
                 payroll.UnpaidAbsentDays = totRaportDays - (payroll.PaidWorkDays - param.DaysPerMonth);
                 payroll.PaidWorkDays = payroll.PaidWorkDays - totRaportDays;
                 reduceRaport = true;
             }*/




        }


        private decimal CalculateBasedOnInstitution(EmployePayroll payroll, GeneralParameter param)
        {
            if (hasError) return -1;
            decimal totals = 0;
            decimal totalS = 0;
            decimal totalsi = 0;
            decimal totalSI = 0;
            decimal totalValue = 0;
            string proccessing = "3) Calculating salary elements per institution: ";
            try
            {



                MultiResult<PayElementContext> res = this.payElManager.GetPayelementContext(payroll.InstitutionID, "", "");
                if (res.HasError) { hasError = true; proccessing += " Error getting salary elements per institution "; return -1; }


                foreach (PayElementContext _insEl in res.ReturnValue)
                {
                    if (!_insEl.Active) continue;
                    if (int.Parse(_insEl.MyElement.ElementType.Key) > 2) continue;
                    var expCntx = new Expression(_insEl, payroll.EmployeeEnrollment.EmployeeEnrolled.Key, payroll.EmployeeEnrollment.Position.Key, payroll.Context);

                    decimal elVal = (decimal)expCntx.CaclulateExpression(_cache, this.payElManager);
                    elVal = Math.Round(elVal, MidpointRounding.AwayFromZero);
                    if (_insEl.IsBasedOnContractedHours)
                    {
                        elVal = Math.Round(this.CalculateValueBasedOnHours(elVal, param), MidpointRounding.AwayFromZero);
                    }

                    _cache.Add(_insEl.MyElement.Code, elVal);

                    EmployeePayrollElement pElement = new EmployeePayrollElement();
                    pElement.MyPayElement = _insEl;
                    pElement.PayElementVersion = _insEl.Version;

                    decimal val = _insEl.MyElement.IsBasedOnWorkingDays ? elVal * payroll.PaidWorkDays / param.DaysPerMonth : elVal;
                    pElement.CalculatedValue = Math.Round(val, MidpointRounding.AwayFromZero);

                    pElement.ElementDescription = _insEl.MyElement.ElementName;
                    pElement.EcconomicalAcc = _insEl.MyElement.EconomicAcc;
                    pElement.ContextBased = true;
                    payrollElementsCalculated.Add(pElement);
                    if (_insEl.IsTaxable)
                        totals += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    else
                        totalS += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    if (_insEl.IsInsured)
                        totalsi += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    else
                        totalSI += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);

                    totalValue += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    if (_insEl.MyElement.IsIncludedInLeaves)
                    {
                        decimal salaryLeaveElement = CalculateSalaryLeaveElement(payroll, elVal, pElement.CalculatedValue);
                        leavesSalary += Math.Round(salaryLeaveElement, MidpointRounding.AwayFromZero);
                    }

                    pElement.CalculatedValue = Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                }

                decimal s = 0;
                decimal S = 0;
                if (_cache.TryGetValue("s", out s))
                {
                    s += totals;

                    _cache.Remove("s");
                    _cache.Add("s", s);
                    _salaryFactors.Remove("s");
                    _salaryFactors.Add("s", s);
                }
                else
                {
                    _cache.Add("s", totals);
                    _salaryFactors.Add("s", totals);
                }

                if (_cache.TryGetValue("S", out S))
                {
                    S += totalS;
                    _cache.Remove("S");
                    _cache.Add("S", S);
                    _salaryFactors.Remove("S");
                    _salaryFactors.Add("S", S);
                }
                else
                {
                    _cache.Add("S", totalS);
                    _salaryFactors.Add("S", totalS);
                }


                decimal si = 0;
                decimal SI = 0;
                if (_cache.TryGetValue("i", out si))
                {
                    si += totalsi;

                    _cache.Remove("i");
                    _cache.Add("i", si);
                    _salaryFactors.Remove("i");
                    _salaryFactors.Add("i", si);
                }
                else
                {
                    _cache.Add("i", totalsi);
                    _salaryFactors.Add("i", totalsi);
                }

                if (_cache.TryGetValue("I", out SI))
                {
                    SI += totalSI;
                    _cache.Remove("I");
                    _cache.Add("I", SI);
                    _salaryFactors.Remove("I");
                    _salaryFactors.Add("I", SI);
                }
                else
                {
                    _cache.Add("I", totalSI);
                    _salaryFactors.Add("I", totalSI);
                }
                proccessing += ErrorMessages._Successfully_Finished_;
                stepProcessing.Add(proccessing);
            }
            catch (Exception exp)
            {
                hasError = true;
                proccessing += ErrorMessages.Operation_Stopped;
                stepProcessing.Add(proccessing);
                throw exp;
            }


            return totalValue;
        }

        private decimal CalculateBasedOnStructure(EmployePayroll payroll, GeneralParameter param)
        {
            if (hasError) return -1;
            decimal totals = 0;
            decimal totalS = 0;
            decimal totalsi = 0;
            decimal totalSI = 0;
            decimal totalValue = 0;
            string proccessing = "4) Calculating salary elements per structure: ";
            try
            {

                MultiResult<PayElementContext> res = this.payElManager.GetPayelementContext(payroll.InstitutionID, payroll.EmployeeEnrollment.Position.OrgID, "");
                if (res.HasError) { hasError = true; proccessing += " Error getting salary elements per structure "; return -1; }

                foreach (PayElementContext _strEl in res.ReturnValue)
                {
                    if (!_strEl.Active) continue;
                    if (int.Parse(_strEl.MyElement.ElementType.Key) > 2) continue;
                    var expCntx = new Expression(_strEl, payroll.EmployeeEnrollment.EmployeeEnrolled.Key, payroll.EmployeeEnrollment.Position.Key, payroll.Context);

                    decimal elVal = (decimal)expCntx.CaclulateExpression(_cache, this.payElManager);
                    elVal = Math.Round(elVal, MidpointRounding.AwayFromZero);
                    if (_strEl.IsBasedOnContractedHours)
                    {
                        elVal = CalculateValueBasedOnHours(elVal, param); // per punonjesit part time
                    }


                    elVal = Math.Round(elVal, MidpointRounding.AwayFromZero);

                    _cache.Add(_strEl.MyElement.Code, elVal);

                    EmployeePayrollElement pElement = new EmployeePayrollElement();
                    pElement.MyPayElement = _strEl;
                    pElement.PayElementVersion = _strEl.Version;

                    decimal val = _strEl.MyElement.IsBasedOnWorkingDays ? elVal * payroll.PaidWorkDays / param.DaysPerMonth : elVal;
                    pElement.CalculatedValue = Math.Round(val, MidpointRounding.AwayFromZero);

                    pElement.ElementDescription = _strEl.MyElement.ElementName;
                    pElement.EcconomicalAcc = _strEl.MyElement.EconomicAcc;
                    pElement.ContextBased = true;
                    payrollElementsCalculated.Add(pElement);
                    if (_strEl.IsTaxable)
                        totals += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    else
                        totalS += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    if (_strEl.IsInsured)
                        totalsi += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    else
                        totalSI += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);

                    totalValue += Math.Round(elVal, MidpointRounding.AwayFromZero);
                    if (_strEl.MyElement.IsIncludedInLeaves)
                    {
                        decimal salaryLeaveElement = CalculateSalaryLeaveElement(payroll, elVal, pElement.CalculatedValue);
                        leavesSalary += Math.Round(salaryLeaveElement, MidpointRounding.AwayFromZero);
                    }

                    pElement.CalculatedValue = Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                }


                decimal s = 0;
                decimal S = 0;
                if (_cache.TryGetValue("s", out s))
                {
                    s += totals;
                    _cache.Remove("s");
                    _cache.Add("s", s);
                    _salaryFactors.Remove("s");
                    _salaryFactors.Add("s", s);
                }
                else
                {
                    _cache.Add("s", totals);
                    _salaryFactors.Add("s", totals);
                }

                if (_cache.TryGetValue("S", out S))
                {
                    S += totalS;
                    _cache.Remove("S");
                    _cache.Add("S", S);
                    _salaryFactors.Remove("S");
                    _salaryFactors.Add("S", S);
                }
                else
                {
                    _cache.Add("S", totalS);
                    _salaryFactors.Add("S", totalS);
                }


                decimal si = 0;
                decimal SI = 0;
                if (_cache.TryGetValue("i", out si))
                {
                    si += totalsi;

                    _cache.Remove("i");
                    _cache.Add("i", si);
                    _salaryFactors.Remove("i");
                    _salaryFactors.Add("i", si);
                }
                else
                {
                    _cache.Add("i", totalsi);
                    _salaryFactors.Add("i", totalsi);
                }

                if (_cache.TryGetValue("I", out SI))
                {
                    SI += totalSI;
                    _cache.Remove("I");
                    _cache.Add("I", SI);
                    _salaryFactors.Remove("I");
                    _salaryFactors.Add("I", SI);
                }
                else
                {
                    _cache.Add("I", totalSI);
                    _salaryFactors.Add("I", totalSI);
                }
                proccessing += ErrorMessages._Successfully_Finished_;
                stepProcessing.Add(proccessing);
            }
            catch (Exception exp)
            {
                hasError = true;
                proccessing += ErrorMessages.Operation_Stopped;
                stepProcessing.Add(proccessing);
                throw exp;
            }
            return totalValue;
        }

        private decimal CalculateBasedOnGroup(EmployePayroll payroll, GeneralParameter param)
        {
            if (hasError) return -1;

            decimal totals = 0;
            decimal totalS = 0;
            decimal totalsi = 0;
            decimal totalSI = 0;
            decimal totalValue = 0;
            string proccessing = "5) Calculating salary elements per group position: ";
            try
            {
                MultiResult<PayElementContext> res = this.payElManager.GetPayelementContext("", "", payroll.EmployeeEnrollment.Position.OrgGrpID);
                if (res.HasError) { hasError = true; proccessing += " Error getting salary elements per group position "; return -1; }

                foreach (PayElementContext _grpEl in res.ReturnValue)
                {
                    if (!_grpEl.Active) continue;
                    if (int.Parse(_grpEl.MyElement.ElementType.Key) > 2) continue;
                    var expCntx = new Expression(_grpEl, payroll.EmployeeEnrollment.EmployeeEnrolled.Key, payroll.EmployeeEnrollment.Position.Key, payroll.Context);

                    decimal elVal = (decimal)expCntx.CaclulateExpression(_cache, this.payElManager);
                    elVal = Math.Round(elVal, MidpointRounding.AwayFromZero);
                    if (_grpEl.IsBasedOnContractedHours)
                    {
                        elVal = CalculateValueBasedOnHours(elVal, param);
                    }

                    elVal = Math.Round(elVal, MidpointRounding.AwayFromZero);

                    _cache.Add(_grpEl.MyElement.Code, elVal);
                    EmployeePayrollElement pElement = new EmployeePayrollElement();
                    pElement.MyPayElement = _grpEl;
                    pElement.PayElementVersion = _grpEl.Version;

                    decimal val = _grpEl.MyElement.IsBasedOnWorkingDays ? elVal * payroll.PaidWorkDays / param.DaysPerMonth : elVal;
                    pElement.CalculatedValue = Math.Round(val, MidpointRounding.AwayFromZero);

                    pElement.ElementDescription = _grpEl.MyElement.ElementName;
                    pElement.EcconomicalAcc = _grpEl.MyElement.EconomicAcc;
                    pElement.ContextBased = true;
                    payrollElementsCalculated.Add(pElement);
                    if (_grpEl.IsTaxable)
                        totals += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    else
                        totalS += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    if (_grpEl.IsInsured)
                        totalsi += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    else
                        totalSI += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    totalValue += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    if (_grpEl.MyElement.IsIncludedInLeaves)
                    {
                        decimal salaryLeaveElement = CalculateSalaryLeaveElement(payroll, elVal, pElement.CalculatedValue);
                        leavesSalary += Math.Round(salaryLeaveElement, MidpointRounding.AwayFromZero);
                    }

                    pElement.CalculatedValue = Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                }


                decimal s = 0;
                decimal S = 0;
                if (_cache.TryGetValue("s", out s))
                {
                    s += totals;
                    _cache.Remove("s");
                    _cache.Add("s", s);
                    _salaryFactors.Remove("s");
                    _salaryFactors.Add("s", s);
                }
                else
                {
                    _cache.Add("s", totals);
                    _salaryFactors.Add("s", totals);
                }

                if (_cache.TryGetValue("S", out S))
                {
                    S += totalS;
                    _cache.Remove("S");
                    _cache.Add("S", S);
                    _salaryFactors.Remove("S");
                    _salaryFactors.Add("S", S);
                }
                else
                {
                    _cache.Add("S", totalS);
                    _salaryFactors.Add("S", totalS);
                }

                decimal si = 0;
                decimal SI = 0;
                if (_cache.TryGetValue("i", out si))
                {
                    si += totalsi;

                    _cache.Remove("i");
                    _cache.Add("i", si);
                    _salaryFactors.Remove("i");
                    _salaryFactors.Add("i", si);
                }
                else
                {
                    _cache.Add("i", totalsi);
                    _salaryFactors.Add("i", totalsi);
                }

                if (_cache.TryGetValue("I", out SI))
                {
                    SI += totalSI;
                    _cache.Remove("I");
                    _cache.Add("I", SI);
                    _salaryFactors.Remove("I");
                    _salaryFactors.Add("I", SI);
                }
                else
                {
                    _cache.Add("I", totalSI);
                    _salaryFactors.Add("I", totalSI);
                }
                proccessing += ErrorMessages._Successfully_Finished_;
                stepProcessing.Add(proccessing);
            }
            catch (Exception exp)
            {
                hasError = true;
                proccessing += ErrorMessages.Operation_Stopped;
                stepProcessing.Add(proccessing);
                throw exp;
            }

            return totalValue;
        }
        private decimal CalculatePrivateInsurance(EmployePayroll payroll)
        {
            string proccessing = " ";
            decimal value = 0;

            try
            {
                EmployeeEnrollment empEnroll = payroll.EmployeeEnrollment;
                //Result<EmployeeEnrollment> something = GetAllPayrollRelatedElements(empEnroll);

                if (empEnroll.MyDeductions.Count == 0) return 0;

                foreach (EmployePayElement el in empEnroll.MyDeductions)
                {

                    if (el.PaymentElement.ElementName == "Sigurimi Vullnetar") value = el.ElementValue;

                }

                if (value == 0) return 0;
                if (value > payroll.GrossSalary) throw new Exception("Private Insurance can not be greater than gross salary");

                decimal vlera, vlerakufi, paga;
               
                var DateOfBirth = payroll.EmployeeEnrollment.EmployeeEnrolled.DateBirth;
                var today = DateTime.Today;
                var age = today.Year - DateOfBirth.Year;

                if (DateOfBirth > today.AddYears(-age)) age--;

                if (age > 50)
                {
                    vlera= (decimal)250000 / 12;
                    paga = (decimal)0.25 * (payroll.GrossSalary);
                    vlerakufi = Math.Min(vlera, paga);
                }
                else
                {
                    vlera = (decimal)200000 / 12;
                    paga = (decimal)0.15 * (payroll.GrossSalary);
                    vlerakufi = Math.Min(vlera, paga);
                }

                if (value > vlerakufi) {

                    decimal diff = value - vlerakufi;
                    value = value - diff;
                }
      
                return value;
            }
            catch (Exception e)
            {
                hasError = true;
                proccessing += ErrorMessages.Operation_Stopped;
                stepProcessing.Add(proccessing);
                throw e;
            }
        }

        private decimal ExceptionForBlind(EmployePayroll payroll)
        {
            string proccessing = " ";
            try
            {
                EmployeeEnrollment empEnroll = payroll.EmployeeEnrollment;
                //Result<EmployeeEnrollment> something = GetAllPayrollRelatedElements(empEnroll);
                if (empEnroll.MyDeductions.Count == 0) return 0;

                foreach (EmployePayElement el in empEnroll.MyDeductions)
                {

                    if (el.PaymentElement.ElementName == "Përjashtim për të verbërit") return el.ElementValue;

                }
                return 0;
            }
            catch (Exception e)
            {
                hasError = true;
                proccessing += ErrorMessages.Operation_Stopped;
                stepProcessing.Add(proccessing);
                throw e;
            }
        }

        private decimal CalculatedEmployeeSupplements(EmployePayroll payroll, GeneralParameter param)
        {
            if (hasError) return -1;
            decimal totals = 0;
            decimal totalS = 0;
            decimal totalsi = 0;
            decimal totalSI = 0;
            decimal totalValue = 0;
            decimal elVal = 0;
            string proccessing = "6) Calculating Supplements: ";

            try
            {

                if (payroll.EmployeeEnrollment.MySupplements.Count > 0)
                {
                    foreach (EmployePayElement el in payroll.EmployeeEnrollment.MySupplements)
                    {
                        if (!el.PaymentElement.Active) continue;

                        if (el.PaymentElement.IsUserDefined)
                        {
                            elVal = el.ElementValue;
                        }
                        else
                        {
                            var exp = new Expression(el.PaymentElement, payroll.EmployeeEnrollment.EmployeeEnrolled.Key, payroll.EmployeeEnrollment.Position.Key, payroll.Context);

                            elVal = (decimal)exp.CaclulateExpression(_cache, this.payElManager);
                            elVal = Math.Round(elVal, MidpointRounding.AwayFromZero);
                        }

                        if (el.PaymentElement.IsBasedOnContractedHours)
                        {
                            elVal = CalculateValueBasedOnHours(elVal, param);
                        }

                        elVal = Math.Round(elVal, MidpointRounding.AwayFromZero);

                        _cache.Add(el.PaymentElement.Code, elVal);

                        EmployeePayrollElement pElement = new EmployeePayrollElement();
                        pElement.MyPayElement = el.PaymentElement;
                        pElement.PayElementVersion = el.PaymentElement.Version;

                        decimal val =
                            el.PaymentElement.IsBasedOnWorkingDays ?
                            elVal * payroll.PaidWorkDays / param.DaysPerMonth
                            : elVal;
                        pElement.CalculatedValue = Math.Round(val, MidpointRounding.AwayFromZero);


                        pElement.ElementDescription = el.PaymentElement.ElementName;
                        pElement.EcconomicalAcc = el.PaymentElement.EconomicAcc;
                        pElement.ContextBased = false;
                        payrollElementsCalculated.Add(pElement);

                        if (el.PaymentElement.IsTaxable)
                            totals += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                        else
                            totalS += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                        if (el.PaymentElement.IsInsured)
                            totalsi += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                        else
                            totalSI += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);

                        totalValue += Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);

                        if (el.PaymentElement.IsIncludedInLeaves)
                        {
                            decimal salaryLeaveElement = CalculateSalaryLeaveElement(payroll, elVal, pElement.CalculatedValue);
                            leavesSalary += Math.Round(salaryLeaveElement, MidpointRounding.AwayFromZero);

                        }

                        pElement.CalculatedValue = Math.Round(pElement.CalculatedValue, MidpointRounding.AwayFromZero);
                    }
                }


                decimal s = 0;
                decimal S = 0;
                if (_cache.TryGetValue("s", out s))
                {
                    s += totals;
                    _cache.Remove("s");
                    _cache.Add("s", s);
                    _salaryFactors.Remove("s");
                    _salaryFactors.Add("s", s);
                }
                else
                {
                    _cache.Add("s", totals);
                    _salaryFactors.Add("s", totals);
                }

                if (_cache.TryGetValue("S", out S))
                {
                    S += totalS;
                    _cache.Remove("S");
                    _cache.Add("S", S);
                    _salaryFactors.Remove("S");
                    _salaryFactors.Add("S", S);
                }
                else
                {
                    _cache.Add("S", totalS);
                    _salaryFactors.Add("S", totalS);
                }

                decimal si = 0;
                decimal SI = 0;
                if (_cache.TryGetValue("i", out si))
                {
                    si += totalsi;

                    _cache.Remove("i");
                    _cache.Add("i", si);
                    _salaryFactors.Remove("i");
                    _salaryFactors.Add("i", si);
                }
                else
                {
                    _cache.Add("i", totalsi);
                    _salaryFactors.Add("i", totalsi);
                }

                if (_cache.TryGetValue("I", out SI))
                {
                    SI += totalSI;
                    _cache.Remove("I");
                    _cache.Add("I", SI);
                    _salaryFactors.Remove("I");
                    _salaryFactors.Add("I", SI);
                }
                else
                {
                    _cache.Add("I", totalSI);
                    _salaryFactors.Add("I", totalSI);
                }
                proccessing += ErrorMessages._Successfully_Finished_;
                stepProcessing.Add(proccessing);
            }
            catch (Exception exp)
            {
                hasError = true;
                proccessing += ErrorMessages.Operation_Stopped;
                stepProcessing.Add(proccessing);
                throw exp;
            }

            return totalValue;
        }

        //***************************** Ndalesat Meposhte***********************
        private decimal CalculateEmployeeDeductions(EmployePayroll payroll)
        {
            if (hasError) return -1;
            decimal totald = 0;
            decimal totalD = 0;
            decimal totalValue = 0;
            decimal totali = 0;
            decimal totalI = 0;
            string proccessing = "7) Calculating Deductions: ";
            try
            {
                if (payroll.EmployeeEnrollment.MyDeductions.Count > 0)
                {
                    // calculate deductions only for elements that are included in payroll
                    foreach (EmployePayElement el in payroll.EmployeeEnrollment.MyDeductions)
                    {
                        // .Where(md => !md.PaymentElement.IsDetaledPayrollIncluded)
                        if (el.PaymentElement.IsUserDefined)
                        {
                            //nese perfshihet ne bordero
                            if (!el.PaymentElement.IsDetaledPayrollIncluded)
                            {
                                if (el.PaymentElement.IsTaxable)
                                    totald += Math.Round(el.ElementValue, MidpointRounding.AwayFromZero);
                                else
                                    totalD += Math.Round(el.ElementValue, MidpointRounding.AwayFromZero);

                                if (el.PaymentElement.IsInsured)
                                    totali += Math.Round(el.ElementValue, MidpointRounding.AwayFromZero);
                                else
                                    totalI += Math.Round(el.ElementValue, MidpointRounding.AwayFromZero);

                                totalValue += Math.Round(el.ElementValue, MidpointRounding.AwayFromZero);
                            }


                            EmployeePayrollElement pElement = new EmployeePayrollElement();
                            pElement.MyPayElement = el.PaymentElement;
                            pElement.PayElementVersion = el.PaymentElement.Version;
                            pElement.CalculatedValue = -el.ElementValue;
                            pElement.ElementDescription = el.PaymentElement.ElementName;
                            pElement.EcconomicalAcc = el.PaymentElement.EconomicAcc;
                            pElement.ContextBased = false;
                            payrollElementsCalculated.Add(pElement);
                        }
                        else
                        {
                            var exp = new Expression(el.PaymentElement, payroll.EmployeeEnrollment.EmployeeEnrolled.Key, payroll.EmployeeEnrollment.Position.Key, payroll.Context);

                            decimal elVal = (decimal)exp.CaclulateExpression(_cache, this.payElManager);
                            elVal = Math.Round(elVal, MidpointRounding.AwayFromZero);

                            _cache.Add(el.PaymentElement.Code, elVal);

                            //nese perfshihet ne bordero
                            if (!el.PaymentElement.IsDetaledPayrollIncluded)
                            {
                                if (el.PaymentElement.IsTaxable)
                                    totald += Math.Round(elVal, MidpointRounding.AwayFromZero);
                                else
                                    totalD += Math.Round(elVal, MidpointRounding.AwayFromZero);

                                if (el.PaymentElement.IsInsured)
                                    totali += Math.Round(elVal, MidpointRounding.AwayFromZero);
                                else
                                    totalI += Math.Round(elVal, MidpointRounding.AwayFromZero);
                                totalValue += Math.Round(elVal, MidpointRounding.AwayFromZero);
                            }

                            EmployeePayrollElement pElement = new EmployeePayrollElement();
                            pElement.MyPayElement = el.PaymentElement;
                            pElement.PayElementVersion = el.PaymentElement.Version;
                            pElement.CalculatedValue = -elVal;
                            pElement.ElementDescription = el.PaymentElement.ElementName;
                            pElement.EcconomicalAcc = el.PaymentElement.EconomicAcc;
                            pElement.ContextBased = false;
                            payrollElementsCalculated.Add(pElement);

                        }
                    }
                }

                decimal d = 0;
                decimal D = 0;
                if (_cache.TryGetValue("d", out d))
                {
                    d += totald;
                    _cache.Remove("d");
                    _cache.Add("d", d);
                    _salaryFactors.Remove("d");
                    _salaryFactors.Add("d", d);
                }
                {
                    _cache.Add("d", totald);
                    _salaryFactors.Add("d", totald);
                }

                if (_cache.TryGetValue("D", out D))
                {
                    D += totalD;
                    _cache.Remove("D");
                    _cache.Add("D", D);
                    _salaryFactors.Remove("D");
                    _salaryFactors.Add("D", D);
                }
                else
                {
                    _cache.Add("D", totalD);
                    _salaryFactors.Add("D", totalD);
                }

                decimal di = 0;
                decimal DI = 0;
                if (_cache.TryGetValue("i", out di))
                {
                    di -= totali;

                    _cache.Remove("i");
                    _cache.Add("i", di);
                    _salaryFactors.Remove("i");
                    _salaryFactors.Add("i", di);
                }
                else
                {
                    _cache.Add("i", -totali);
                    _salaryFactors.Add("i", -totali);
                }
                proccessing += ErrorMessages._Successfully_Finished_;
                stepProcessing.Add(proccessing);
            }
            catch (Exception exp)
            {
                hasError = true;
                proccessing += ErrorMessages.Operation_Stopped;
                stepProcessing.Add(proccessing);
                throw exp;
            }

            payroll.Deductions += totalValue;
            return totalValue;
        }

        private void CalculateInsuredAmount()
        {
            decimal mamount = 0;
            decimal iamount = 0;
            string proccessing = "8) Calculating Insured Amount: ";

            if (_salaryFactors.TryGetValue("m", out mamount) &&
                _salaryFactors.TryGetValue("i", out iamount)
                )
            {
                totalInsuredAmount = Math.Round(mamount, MidpointRounding.AwayFromZero)
                    + Math.Round(iamount, MidpointRounding.AwayFromZero);
                this._cache.Add("C", totalInsuredAmount);
                this._salaryFactors.Add("C", totalInsuredAmount);
                proccessing += "Successfully Calculated";
            }
            else
                throw new ApplicationException(proccessing + "Mungojne elementet perberes te llogaritjes se shumes qe do sigurohet");
        }

        private void CalculateTaxAmount()
        {
            decimal mamount = 0;
            decimal tamount = 0;
            decimal damount = 0;
            decimal PrivateInsurance = 0;

            string proccessing = "8) Calculating Taxed Amount: ";

            if (_salaryFactors.TryGetValue("m", out mamount) &&
                _salaryFactors.TryGetValue("s", out tamount)
                && _salaryFactors.TryGetValue("PrivateInsurance", out PrivateInsurance)
                //&&
                //_salaryFactors.TryGetValue("d", out damount)
                )
            {
                totalTaxedAmount =
                    Math.Round(mamount, MidpointRounding.AwayFromZero)
                    + Math.Round(tamount, MidpointRounding.AwayFromZero)
                    - Math.Round(damount, MidpointRounding.AwayFromZero)
                    - Math.Round(PrivateInsurance, MidpointRounding.AwayFromZero);

                this._cache.Add("T", totalTaxedAmount);
                this._salaryFactors.Add("T", totalTaxedAmount);
                proccessing += "Successfully Calculated";
            }

            else
                throw new ApplicationException(proccessing + "Mungojne elementet perberes te llogaritjes se shumes qe do te tatohet");
            //}
        }

        private decimal CalculateSocialInsurance(EmployePayroll payroll)
        {
            if (hasError) return -1;
            decimal total = 0;
            decimal totalEmp = 0;
            socialinsEmployer = 0;
            string proccessing = "9) Calculating Social Insurance: ";
            try
            {
                MultiResult<PayElement> res = this.payElManager.GetPaymentElementByType("5");
                if (res.HasError) { hasError = true; proccessing += " Error getting Social Insurance"; throw new ApplicationException(proccessing + res.MessageResult); }



                if (res.ReturnValue.Count > 2)
                {
                    throw new ApplicationException("There are too many social insurance configured");
                }


                foreach (PayElement el in res.ReturnValue)
                {
                    if (!el.Active) continue;
                    if (el.IsUserDefined)
                    {
                        total += Math.Round((el.Value.HasValue) ? (decimal)el.Value : 0, MidpointRounding.AwayFromZero);
                        EmployeePayrollElement pElement = new EmployeePayrollElement();
                        pElement.MyPayElement = el;
                        pElement.PayElementVersion = el.Version;
                        pElement.CalculatedValue = -((el.Value.HasValue) ? (decimal)el.Value : 0);
                        pElement.ElementDescription = el.ElementName;
                        pElement.EcconomicalAcc = el.EconomicAcc;
                        pElement.ContextBased = false;
                        payrollElementsCalculated.Add(pElement);
                    }
                    else
                    {
                        var exp = new Expression(el, payroll.EmployeeEnrollment.EmployeeEnrolled.Key, payroll.EmployeeEnrollment.Position.Key, payroll.Context);

                        decimal elVal = (decimal)exp.CaclulateExpression(_cache, this.payElManager);
                        elVal = Math.Round(elVal, MidpointRounding.AwayFromZero);

                        if (!_cache.ContainsKey(el.Code))
                            _cache.Add(el.Code, elVal);
                        else
                            _cache[el.Code] = elVal;

                        EmployeePayrollElement pElement = new EmployeePayrollElement();

                        var resultElement = payrollElementsCalculated.Where(x => x.MyPayElement.Key == el.Key && x.ContextBased == el.IsContextBasedOnly);
                        if (resultElement.Count() > 0)
                            pElement = resultElement.FirstOrDefault();


                        pElement.MyPayElement = el;
                        pElement.PayElementVersion = el.Version;
                        pElement.CalculatedValue = -Math.Round(elVal, MidpointRounding.AwayFromZero);
                        pElement.ElementDescription = el.ElementName;
                        pElement.EcconomicalAcc = el.EconomicAcc;
                        pElement.ContextBased = false;
                        if (!payrollElementsCalculated.Contains(pElement))
                            payrollElementsCalculated.Add(pElement);

                        if (!el.IsEmployer)
                        {
                            total += Math.Round(elVal, MidpointRounding.AwayFromZero);
                            Result<decimal> myres = this.payElManager.GetPayElementRangeBaseValue(el.Key, totalInsuredAmount);
                            if (myres.HasError) { throw new ApplicationException(proccessing + " Can not get the Base Value for calculating social insurance"); }
                            socialinsSalary = myres.ReturnValue;
                        }
                        else
                        {
                            socialinsEmployer += elVal;
                        }
                    }
                }

                socialinsEmployee = total;
                proccessing += ErrorMessages._Successfully_Finished_;
                stepProcessing.Add(proccessing);
            }
            catch (Exception exp)
            {
                hasError = true;
                proccessing += ErrorMessages.Operation_Stopped;
                stepProcessing.Add(proccessing);
                throw exp;
            }
            return total;
        }

        private decimal CalculateHealthInsurance(EmployePayroll payroll)
        {
            if (hasError) return -1;
            decimal total = 0;
            healthInsEmployer = 0;
            string proccessing = "10) Calculating Health Insurance: ";

            try
            {
                MultiResult<PayElement> res = this.payElManager.GetPaymentElementByType("6");
                if (res.HasError) { hasError = true; proccessing += " Error getting Social Insurance"; throw new ApplicationException(proccessing + res.MessageResult); }



                if (res.ReturnValue.Count > 2)
                {
                    throw new ApplicationException("There are too many Health insurance configured");
                }


                foreach (PayElement el in res.ReturnValue)
                {
                    if (!el.Active) continue;
                    if (el.IsUserDefined)
                    {
                        total += Math.Round((el.Value.HasValue) ? (decimal)el.Value : 0, MidpointRounding.AwayFromZero);
                        EmployeePayrollElement pElement = new EmployeePayrollElement();
                        pElement.MyPayElement = el;
                        pElement.PayElementVersion = el.Version;
                        pElement.CalculatedValue = -((el.Value.HasValue) ? (decimal)el.Value : 0);
                        pElement.ElementDescription = el.ElementName;
                        pElement.EcconomicalAcc = el.EconomicAcc;
                        pElement.ContextBased = false;
                        payrollElementsCalculated.Add(pElement);
                    }
                    else
                    {
                        var exp = new Expression(el, payroll.EmployeeEnrollment.EmployeeEnrolled.Key, payroll.EmployeeEnrollment.Position.Key, payroll.Context);
                        decimal elVal = Math.Round((decimal)exp.CaclulateExpression(_cache, this.payElManager), MidpointRounding.AwayFromZero);
                        if (!_cache.ContainsKey(el.Code))
                            _cache.Add(el.Code, elVal);
                        else
                            _cache[el.Code] = elVal;
                        EmployeePayrollElement pElement = new EmployeePayrollElement();

                        var resultElement = payrollElementsCalculated.Where(x => x.MyPayElement.Key == el.Key && x.ContextBased == el.IsContextBasedOnly);
                        if (resultElement.Count() > 0)
                            pElement = resultElement.FirstOrDefault();


                        pElement.MyPayElement = el;
                        pElement.PayElementVersion = el.Version;
                        pElement.CalculatedValue = -Math.Round(elVal, MidpointRounding.AwayFromZero);
                        pElement.ElementDescription = el.ElementName;
                        pElement.EcconomicalAcc = el.EconomicAcc;
                        pElement.ContextBased = false;
                        if (!payrollElementsCalculated.Contains(pElement))
                            payrollElementsCalculated.Add(pElement);

                        if (!el.IsEmployer)
                        {
                            total += Math.Round(elVal, MidpointRounding.AwayFromZero);
                            Result<decimal> myres = this.payElManager.GetPayElementRangeBaseValue(el.Key, totalInsuredAmount);
                            if (myres.HasError) { throw new ApplicationException(proccessing + " Can not get the Base Value for calculating health insurance"); }
                            healthInsSalary = myres.ReturnValue;
                        }
                        else
                        {
                            healthInsEmployer += Math.Round(elVal, MidpointRounding.AwayFromZero);
                        }
                    }
                }

                healthInsEmployee = total;
                proccessing += ErrorMessages._Successfully_Finished_;
                stepProcessing.Add(proccessing);
            }
            catch (Exception exp)
            {
                hasError = true;
                proccessing += ErrorMessages.Operation_Stopped;
                stepProcessing.Add(proccessing);
                throw exp;
            }
            return total;
        }

        private decimal CalcualteAdditionalInsurance(EmployePayroll payroll)
        {
            if (hasError) return -1;
            decimal total = 0;
            string proccessing = "10) Calculating Additional Insurance: ";
            try
            {

                MultiResult<PayElement> myres = this.payElManager.GetPaymentElementByType("7");
                if (myres.HasError) { hasError = true; proccessing += " Error getting additional Insurance"; throw new ApplicationException(proccessing + myres.MessageResult); }


                foreach (PayElement el in myres.ReturnValue)
                {
                    decimal vlr = 0;
                    if (_cache.TryGetValue(el.Code, out vlr))
                    {
                        total += Math.Round(vlr, MidpointRounding.AwayFromZero);
                        continue;
                    }
                    if (el.IsContextBasedOnly)
                    {
                        decimal payElementWithContext = CalculatePayElementOnContexts(el, payroll);
                        total += Math.Round(payElementWithContext, MidpointRounding.AwayFromZero);
                        continue;
                    }

                    if (el.IsUserDefined)
                    {
                        total += Math.Round((el.Value.HasValue) ? (decimal)el.Value : 0, MidpointRounding.AwayFromZero);
                        EmployeePayrollElement pElement = new EmployeePayrollElement();
                        pElement.MyPayElement = el;
                        pElement.PayElementVersion = el.Version;
                        pElement.CalculatedValue = -((el.Value.HasValue) ? el.Value.Value : 0);
                        pElement.ElementDescription = el.ElementName;
                        pElement.EcconomicalAcc = el.EconomicAcc;
                        pElement.ContextBased = false;
                        payrollElementsCalculated.Add(pElement);
                    }
                    else
                    {
                        var exp = new Expression(el, payroll.EmployeeEnrollment.EmployeeEnrolled.Key, payroll.EmployeeEnrollment.Position.Key, payroll.Context);
                        decimal elVal = Math.Round((decimal)exp.CaclulateExpression(_cache, this.payElManager), MidpointRounding.AwayFromZero);
                        _cache.Add(el.Code, elVal);
                        EmployeePayrollElement pElement = new EmployeePayrollElement();

                        var resultElement = payrollElementsCalculated.Where(x => x.MyPayElement.Key == el.Key && x.ContextBased == el.IsContextBasedOnly);
                        if (resultElement.Count() > 0)
                            pElement = resultElement.FirstOrDefault();

                        pElement.MyPayElement = el;
                        pElement.PayElementVersion = el.Version;
                        pElement.CalculatedValue = -Math.Round(elVal, MidpointRounding.AwayFromZero);
                        pElement.ElementDescription = el.ElementName;
                        pElement.EcconomicalAcc = el.EconomicAcc;
                        pElement.ContextBased = false;

                        if (!payrollElementsCalculated.Contains(pElement))
                            payrollElementsCalculated.Add(pElement);

                        if (!el.IsEmployer)
                        {
                            total += Math.Round(elVal, MidpointRounding.AwayFromZero);
                        }
                    }
                }

                addInsTotal = total;
                proccessing += ErrorMessages._Successfully_Finished_;
                stepProcessing.Add(proccessing);
            }
            catch (Exception exp)
            {
                hasError = true;
                proccessing += ErrorMessages.Operation_Stopped;
                stepProcessing.Add(proccessing);
                throw exp;
            }

            return total;
        }

        private decimal CalculateTax(EmployePayroll payroll)
        {
            if (hasError) return -1;
            decimal total = 0;
            string proccessing = "11) Calculating Taxes: ";
            decimal PrivateInsurance = 0;
            decimal ExceptionForBlind = 0;
            decimal elVal = 0;
            try
            {
                MultiResult<PayElement> res = this.payElManager.GetPaymentElementByType("4");
                if (res.HasError) { hasError = true; proccessing += " Error getting Taxes"; throw new ApplicationException(proccessing + res.MessageResult); }




                foreach (PayElement el in res.ReturnValue)
                {
                    if (el.Code == "E8")
                    {
                        _salaryFactors.TryGetValue("PrivateInsurance", out PrivateInsurance);
                        _salaryFactors.TryGetValue("ExceptionForBlind", out ExceptionForBlind);
                    }

                    if (!el.Active) continue;
                    if (el.IsUserDefined)
                    {
                        total += Math.Round((el.Value.HasValue) ? (decimal)el.Value : 0, MidpointRounding.AwayFromZero);
                        EmployeePayrollElement pElement = new EmployeePayrollElement();
                        pElement.MyPayElement = el;
                        pElement.PayElementVersion = el.Version;
                        pElement.CalculatedValue = -((el.Value.HasValue) ? el.Value.Value : 0);
                        pElement.ElementDescription = el.ElementName;
                        pElement.EcconomicalAcc = el.EconomicAcc;
                        pElement.ContextBased = false;
                        payrollElementsCalculated.Add(pElement);
                    }
                    else
                    {
                        var exp = new Expression(el, payroll.EmployeeEnrollment.EmployeeEnrolled.Key, payroll.EmployeeEnrollment.Position.Key, payroll.Context);
                        if (ExceptionForBlind == 1) elVal = 0;
                        else
                            elVal = (decimal)exp.CaclulateExpression(_cache, this.payElManager);
                        elVal = Math.Round(elVal, MidpointRounding.AwayFromZero);

                        if (!_cache.ContainsKey(el.Code))
                            _cache.Add(el.Code, elVal);
                        else
                            _cache[el.Code] = elVal;

                        EmployeePayrollElement pElement = new EmployeePayrollElement();
                        var resultElement = payrollElementsCalculated.Where(x => x.MyPayElement.Key == el.Key && x.ContextBased == el.IsContextBasedOnly);
                        if (resultElement.Count() > 0)
                            pElement = resultElement.FirstOrDefault();

                        pElement.MyPayElement = el;
                        pElement.PayElementVersion = el.Version;
                        pElement.CalculatedValue = -Math.Round(elVal, MidpointRounding.AwayFromZero);
                        pElement.ElementDescription = el.ElementName;
                        pElement.EcconomicalAcc = el.EconomicAcc;
                        pElement.ContextBased = false;
                        if (!payrollElementsCalculated.Contains(pElement))
                            payrollElementsCalculated.Add(pElement);


                        if (!el.IsEmployer)
                        {
                            total += Math.Round(elVal);
                            Result<decimal> myres = this.payElManager.GetPayElementRangeBaseValue(el.Key, totalInsuredAmount);
                            if (myres.HasError) { throw new ApplicationException(proccessing + " Can not get the Base Value for calculating taxes"); }
                            taxSalary = myres.ReturnValue;
                        }
                    }
                }

                taxTotal = total;
                proccessing += ErrorMessages._Successfully_Finished_;
                stepProcessing.Add(proccessing);
            }
            catch (Exception exp)
            {
                hasError = true;
                proccessing += ErrorMessages.Operation_Stopped;
                stepProcessing.Add(proccessing);
                throw exp;
            }
            return total;
        }

        private List<PayElementContext> GetMostDefinedPayelementContext(List<PayElementContext> payElementCtxs)
        {
            return payElementCtxs
                 .Where(a => a.Active)
                 .Select(elementCtx => new { ElementCtx = elementCtx, Priority = GetPayelementContextPriority(elementCtx) })
                 .GroupBy(element => element.ElementCtx.ElementID)
                 .Select(obj => obj.OrderBy(ord => ord.Priority).First().ElementCtx)
                 .ToList();
        }
        private int GetPayelementContextPriority(PayElementContext payElementCtx)
        {
            int priority = 0;
            if (!string.IsNullOrEmpty(payElementCtx.InstitutionID) && !string.IsNullOrEmpty(payElementCtx.OrgStructureID) && !string.IsNullOrEmpty(payElementCtx.OrgGroupID))
            {
                priority = 1;
            }
            else if (!string.IsNullOrEmpty(payElementCtx.InstitutionID) && string.IsNullOrEmpty(payElementCtx.OrgStructureID) && !string.IsNullOrEmpty(payElementCtx.OrgGroupID))
            {
                priority = 2;
            }
            else if (string.IsNullOrEmpty(payElementCtx.InstitutionID) && string.IsNullOrEmpty(payElementCtx.OrgStructureID) && !string.IsNullOrEmpty(payElementCtx.OrgGroupID))
            {
                priority = 3;
            }
            else if (!string.IsNullOrEmpty(payElementCtx.InstitutionID) && !string.IsNullOrEmpty(payElementCtx.OrgStructureID) && string.IsNullOrEmpty(payElementCtx.OrgGroupID))
            {
                priority = 4;
            }
            else if (!string.IsNullOrEmpty(payElementCtx.InstitutionID) && string.IsNullOrEmpty(payElementCtx.OrgStructureID) && string.IsNullOrEmpty(payElementCtx.OrgGroupID))
            {
                priority = 5;
            }
            return priority;
        }
        private decimal CalculatePayElementOnContexts(PayElement el, EmployePayroll payroll)
        {
            decimal total = 0;

            List<PayElementContext> payelementscntx = new List<PayElementContext>();

            MultiResult<PayElementContext> res = this.payElManager.GetPayelementContext(el.Key, payroll.InstitutionID, payroll.EmployeeEnrollment.Position.OrgID, payroll.EmployeeEnrollment.Position.OrgGrpID);
            if (res.HasError) { hasError = true; throw new ApplicationException(res.MessageResult); }
            payelementscntx.AddRange(res.ReturnValue);
            res = this.payElManager.GetPayelementContext(el.Key, payroll.InstitutionID, payroll.EmployeeEnrollment.Position.OrgID, "");
            if (res.HasError) { hasError = true; throw new ApplicationException(res.MessageResult); }
            payelementscntx.AddRange(res.ReturnValue);
            res = this.payElManager.GetPayelementContext(el.Key, payroll.InstitutionID, "", "");
            if (res.HasError) { hasError = true; throw new ApplicationException(res.MessageResult); }
            payelementscntx.AddRange(res.ReturnValue);
            res = this.payElManager.GetPayelementContext(el.Key, "", "", payroll.EmployeeEnrollment.Position.OrgGrpID);
            if (res.HasError) { hasError = true; throw new ApplicationException(res.MessageResult); }
            payelementscntx.AddRange(res.ReturnValue);
            res = this.payElManager.GetPayelementContext(el.Key, payroll.InstitutionID, "", payroll.EmployeeEnrollment.Position.OrgGrpID);
            if (res.HasError) { hasError = true; throw new ApplicationException(res.MessageResult); }
            payelementscntx.AddRange(res.ReturnValue);

            /// Handle not User Defined ContextPayElements
            payelementscntx = (el.IsUserDefined) ? payelementscntx : GetMostDefinedPayelementContext(payelementscntx);
            ///

            foreach (PayElementContext elCntx in payelementscntx)
            {
                if (!elCntx.Active) continue;
                if (elCntx.IsUserDefined)
                {
                    total += Math.Round((elCntx.Value.HasValue) ? (decimal)elCntx.Value : 0, MidpointRounding.AwayFromZero);
                    EmployeePayrollElement pElement = new EmployeePayrollElement();
                    pElement.MyPayElement = elCntx.MyElement;
                    pElement.PayElementVersion = elCntx.Version;
                    pElement.CalculatedValue = -((elCntx.Value.HasValue) ? elCntx.Value.Value : 0);
                    pElement.ElementDescription = elCntx.MyElement.ElementName;
                    pElement.EcconomicalAcc = elCntx.MyElement.EconomicAcc;
                    pElement.ContextBased = true;
                    payrollElementsCalculated.Add(pElement);
                }
                else
                {
                    var exp = new Expression(elCntx, payroll.EmployeeEnrollment.EmployeeEnrolled.Key, payroll.EmployeeEnrollment.Position.Key, payroll.Context);

                    decimal elVal = (decimal)exp.CaclulateExpression(_cache, this.payElManager);
                    elVal = Math.Round(elVal, MidpointRounding.AwayFromZero);

                    _cache.Add(elCntx.MyElement.Code, elVal);
                    EmployeePayrollElement pElement = new EmployeePayrollElement();
                    pElement.MyPayElement = elCntx.MyElement;
                    pElement.PayElementVersion = elCntx.Version;
                    pElement.CalculatedValue = -Math.Round(elVal, MidpointRounding.AwayFromZero);
                    pElement.ElementDescription = elCntx.MyElement.ElementName;
                    pElement.EcconomicalAcc = elCntx.MyElement.EconomicAcc;
                    pElement.ContextBased = true;
                    payrollElementsCalculated.Add(pElement);

                    if (!elCntx.IsEmployer)
                    {
                        total += Math.Round(elVal, MidpointRounding.AwayFromZero);
                    }
                }
            }

            return total;
        }

        private string SerializePayElementJSON(EmployeePayrollElement el)
        {
            string jsonSTR = "{";
            if (el.MyPayElement != null)
            {
                if (el.MyPayElement is PayElementContext)
                {
                    jsonSTR = jsonSTR + "\"Eid\":" + ((PayElementContext)el.MyPayElement).MyElement.Key + ",";
                    jsonSTR = jsonSTR + "\"CntxId\":" + ((PayElementContext)el.MyPayElement).Key + ",";
                    jsonSTR = jsonSTR + "\"Version\":" + ((PayElementContext)el.MyPayElement).Version.ToString() + ",";
                    jsonSTR = jsonSTR + "\"Code\":" + ((PayElementContext)el.MyPayElement).MyElement.Code + ",";
                    jsonSTR = jsonSTR + "\"ElVal\":" + (((PayElementContext)el.MyPayElement).Value.HasValue ? ((PayElementContext)el.MyPayElement).Value.ToString() : "") + ",";
                    jsonSTR = jsonSTR + "\"Expression\":" + ((PayElementContext)el.MyPayElement).ExpressionCalculation + ",";
                    jsonSTR = jsonSTR + "\"Procedure\":" + ((PayElementContext)el.MyPayElement).ProcedureName + ",";
                    jsonSTR = jsonSTR + "\"AccCode\":" + ((PayElementContext)el.MyPayElement).MyElement.EconomicAcc + ",";
                    jsonSTR = jsonSTR + "\"Description\":" + ((PayElementContext)el.MyPayElement).MyElement.ElementName + ",";
                    jsonSTR = jsonSTR + "\"PayGrdId\":" + ((PayElementContext)el.MyPayElement).PayGradID + ",";
                    jsonSTR = jsonSTR + "\"GrpID\":" + ((PayElementContext)el.MyPayElement).OrgGroupID + ",";
                    jsonSTR = jsonSTR + "\"StructID\":" + ((PayElementContext)el.MyPayElement).OrgStructureID + ",";
                    jsonSTR = jsonSTR + "\"InstitutionID\":" + ((PayElementContext)el.MyPayElement).InstitutionID + ",";

                }
                else
                {
                    jsonSTR = jsonSTR + "Eid:" + el.MyPayElement.Key + ",";
                    //jsonSTR = jsonSTR + "\"CntxId\":" +",";
                    jsonSTR = jsonSTR + "\"Version\":" + el.MyPayElement.Version.ToString() + ",";
                    jsonSTR = jsonSTR + "\"Code\":" + el.MyPayElement.Code + ",";
                    jsonSTR = jsonSTR + "\"ElVal\":" + (el.MyPayElement.Value.HasValue ? el.MyPayElement.Value.ToString() : "") + ",";
                    jsonSTR = jsonSTR + "\"Expression\":" + el.MyPayElement.ExpressionCalculation + ",";
                    jsonSTR = jsonSTR + "\"Procedure\":" + el.MyPayElement.ProcedureName + ",";
                    jsonSTR = jsonSTR + "\"AccCode\":" + el.MyPayElement.EconomicAcc + ",";
                    jsonSTR = jsonSTR + "\"Description\":" + el.MyPayElement.ElementName + ",";
                }
            }
            return jsonSTR = jsonSTR + "}";

        }

        private Result<EmployePayroll> PersistPayroll(EmployePayroll payroll)
        {
            Result<EmployePayroll> res = null;
            try
            {


                if (!string.IsNullOrEmpty(payroll.Key))
                {


                    payroll.ModifiedBy = ucntx.UserID;
                    payroll.ModifiedOn = payroll.CreatedOn;
                    payroll.ModifiedIP = ucntx.IP;

                    repository.UpdatePayroll(payroll);
                }
                else
                {

                    payroll.CreatedBy = ucntx.UserID;
                    payroll.CreatedOn = DateTime.Now;
                    payroll.CreatedIP = ucntx.IP;
                    payroll.ModifiedBy = ucntx.UserID;
                    payroll.ModifiedOn = payroll.CreatedOn;
                    payroll.ModifiedIP = ucntx.IP;
                    repository.AddNewPayroll(payroll);
                }


                res = new Result<EmployePayroll>(payroll, false, ErrorMessages.Employe_Payroll_Succesfully_updated);
            }
            catch (Exception exp)
            {
                res = new Result<EmployePayroll>(null, true, exp);
                Logger.Error(exp);
            }

            return res;
        }

        private void FillPayrollWithCalculatedFields(ref EmployePayroll payroll)
        {
            CalculateInsuredAmount();

            grossTotal = totalSalaryMonth;
            payroll.GrossSalary = Math.Round(grossTotal, MidpointRounding.AwayFromZero);

           
            
            decimal privateInsurance = CalculatePrivateInsurance(payroll);
            _cache.Add("PrivateInsurance", privateInsurance);
            _salaryFactors.Add("PrivateInsurance", privateInsurance);

            if (ExceptionForBlind(payroll) == 1)
            {
                _cache.Add("ExceptionForBlind", 1);
                _salaryFactors.Add("ExceptionForBlind", 1);
            }
            else
            {
                _cache.Add("ExceptionForBlind", 0);
                _salaryFactors.Add("ExceptionForBlind", 0);
            }
            CalculateTaxAmount();

            payroll.TaxSalary = Math.Round(totalTaxedAmount, MidpointRounding.AwayFromZero);
            payroll.ContribSalary = Math.Round(totalInsuredAmount, MidpointRounding.AwayFromZero);

            decimal deductions = CalculateEmployeeDeductions(payroll);
            payroll.Deductions = Math.Round(deductions, MidpointRounding.AwayFromZero);

            CalculateSocialInsurance(payroll);
            CalculateHealthInsurance(payroll);
            CalcualteAdditionalInsurance(payroll);
            CalculateTax(payroll);

            payroll.SocialInsuranceEmployee = Math.Round(socialinsEmployee, MidpointRounding.AwayFromZero);
            payroll.SocialInsuranceEmployer = Math.Round(socialinsEmployer, MidpointRounding.AwayFromZero);
            payroll.HealthInsuranceEmployee = Math.Round(healthInsEmployee, MidpointRounding.AwayFromZero);
            payroll.HealthInsuranceEmployer = Math.Round(healthInsEmployer, MidpointRounding.AwayFromZero);
            payroll.AdditionalInsurance = Math.Round(addInsTotal, MidpointRounding.AwayFromZero);
            payroll.IncomeTax = Math.Round(taxTotal, MidpointRounding.AwayFromZero);

            netTotal =
                  payroll.GrossSalary
                - payroll.Deductions
                - payroll.SocialInsuranceEmployee
                - payroll.HealthInsuranceEmployee
                - payroll.AdditionalInsurance
                - payroll.IncomeTax;

            payroll.NetSalary = Math.Round(netTotal, MidpointRounding.AwayFromZero);
        }

        #endregion


        #region Refactored Methods
        private DateTime Max(DateTime left, DateTime right)
        {
            return left > right ? left : right;
        }

        private DateTime Min(DateTime left, DateTime right)
        {
            return left < right ? left : right;
        }


        private int GetNumberOfHolidayDaysInThisPeriod(EmployePayroll payroll, DateTime pStart, DateTime pEnd)
        {
            var minDate = Max(pStart, payroll.EmployeeEnrollment.StartFrom);
            var maxDate = Min(pEnd, payroll.EmployeeEnrollment.EndTo ?? DateTime.MaxValue);
            if (payroll.PeriodHolidays != null)
            {
                int nrHolidayDays = payroll.PeriodHolidays.Count(h => minDate <= h.Day && h.Day <= maxDate);
                return nrHolidayDays;
            }
            else return 0;

            //int nrDays = 0;
            //if (empStart.Date <= pStart.Date)  //Rasti 1
            //{
            //    if (empEnd != null)
            //    {
            //        if (empEnd.Value.Date >= pEnd.Date) // b
            //        {
            //            nrDays = holidayManager.GetNumberOfHolidayDaysInThisPeriod(pStart, pEnd);
            //        }
            //        else //a
            //        {
            //            nrDays = holidayManager.GetNumberOfHolidayDaysInThisPeriod(pStart, (DateTime)empEnd);
            //        }
            //    }
            //    else// si b
            //    {
            //        nrDays = holidayManager.GetNumberOfHolidayDaysInThisPeriod(pStart, pEnd);
            //    }

            //}
            //else  // Rasti 2
            //{
            //    if (empEnd != null)
            //    {
            //        if (empEnd.Value.Date >= pEnd.Date) //b
            //        {
            //            nrDays = holidayManager.GetNumberOfHolidayDaysInThisPeriod(empStart, pEnd);
            //        }
            //        else//a
            //        {
            //            nrDays = holidayManager.GetNumberOfHolidayDaysInThisPeriod(empStart, (DateTime)empEnd);
            //        }
            //    }
            //    else // si B
            //    {
            //        nrDays = holidayManager.GetNumberOfHolidayDaysInThisPeriod(empStart, pEnd);
            //    }
            //}
            //return nrDays;
        }

        /// <summary>
        /// Note pEnd is the start of next period, that is not the end of the month, but the first of next month
        /// </summary>
        /// <param name="param"></param>
        /// <param name="pStart"></param>
        /// <param name="pEnd"></param>
        /// <param name="empStart"></param>
        /// <param name="empEnd"></param>
        /// <param name="totalPeriodDays"></param>
        /// <param name="employmentStartsOrEndsWithInPeriod"></param>
        /// <returns></returns>
        private int CalculateEmployeeWorkHours(GeneralParameter param, DateTime pStart, DateTime pEnd, DateTime empStart, DateTime? empEnd, int totalPeriodDays, out bool employmentStartsOrEndsWithInPeriod)
        {
            int workingDays = 0;
            employmentStartsOrEndsWithInPeriod = false;

            // employment has started before or at the beginning of this period
            if (empStart.Date <= pStart.Date)
            {
                // employment has ended
                if (empEnd.HasValue)
                {
                    // employment has ended at or after the end of this period
                    if (empEnd.Value >= pEnd.AddDays(-1))
                    {
                        workingDays = param.DaysPerMonth;
                    }
                    else // employment has ended before the end of this period, find the the difference from the start of the period
                    {
                        Double differenceInDays = empEnd.Value.AddDays(1).Subtract(pStart).TotalDays;

                        // how many days of employment has this employee within this period
                        int totalDays = Convert.ToInt32(Math.Round(differenceInDays, 0, MidpointRounding.AwayFromZero));

                        // how many days was he supposed to work, excluding hollidays
                        int nrDays = FindNrWorkDays(pStart, totalDays);

                        workingDays = nrDays >= param.DaysPerMonth ? param.DaysPerMonth : nrDays;
                        employmentStartsOrEndsWithInPeriod = true;
                    }
                } // employment has not ended , the whole period is counted 
                else
                {
                    if (totalPeriodDays <= param.DaysPerMonth)
                        workingDays = param.DaysPerMonth;
                    else
                        workingDays = totalPeriodDays;
                }


            } // employment has started after the beginning of this period 
            else
            {
                Double differenceInDays;
                // employment has started after the beginning of this period and left before the end of period
                if (empEnd != null && empEnd.Value < pEnd.Date)
                {
                    // find time from the beginning of employment to the end of the employment 
                    differenceInDays = empEnd.Value.Subtract(empStart).TotalDays + 1;
                } // employment has started after the beginning of this period and left after the end of period
                else
                {
                    // find time from the beginning of employment to the end of the period 
                    differenceInDays = pEnd.Subtract(empStart).TotalDays;
                }

                // days the employee may have been working, or in leave
                int totalDays = Convert.ToInt32(Math.Round(differenceInDays, 0, MidpointRounding.AwayFromZero));

                // remove holidays and weekends
                int nrDays = FindNrWorkDays(empStart, totalDays);

                if (posCount > 1 || (posCount == 1 && totalPeriodDays == nrDays))
                {
                    if (totalPeriodDays < param.DaysPerMonth)
                    {
                        nrDays = nrDays + (param.DaysPerMonth - totalPeriodDays);
                    }
                    else if (totalPeriodDays > param.DaysPerMonth)
                    {
                        nrDays = nrDays - (totalPeriodDays - param.DaysPerMonth);
                    }
                }
                workingDays = nrDays;
                employmentStartsOrEndsWithInPeriod = true;

            }

            return workingDays;
        }

        private int FindNrWorkDays(DateTime startFrom, int totalDays)
        {
            int nrDays = 0;
            DateTime workDate = startFrom;
            for (int i = 1; i <= totalDays; i++)
            {

                if (workDate.DayOfWeek != DayOfWeek.Saturday && workDate.DayOfWeek != DayOfWeek.Sunday)
                {
                    nrDays++;

                }
                workDate = workDate.AddDays(1);
            }

            return nrDays;
        }

        public int FindNrWorkDaysPerPeriod(DateTime pStart, DateTime pEnd)
        {
            int nrDays = 0;
            DateTime workDate = pStart;
            for (; workDate < pEnd;)
            {

                if (workDate.DayOfWeek != DayOfWeek.Saturday && workDate.DayOfWeek != DayOfWeek.Sunday)
                {
                    nrDays++;

                }
                workDate = workDate.AddDays(1);
            }

            return nrDays;
        }
        private decimal CalculateSalaryLeaveElement(EmployePayroll payroll, decimal originalVl, decimal calulatedVl)
        {
            decimal retVl = 0;

            if (payroll.PaidWorkDays == 0 && hoursOnLeave > 0)
            {
                retVl = originalVl;
            }
            else
                retVl = calulatedVl;


            return retVl;

        }
        #endregion
    }
}
