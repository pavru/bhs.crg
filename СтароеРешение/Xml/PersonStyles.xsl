<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:ct="urn:BimHouse:CommonDataType" xmlns:cfg="urn:BimHouse:PersonUtilConfig" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:bf='urn:BimHouse:XslFunctions'>
	<xsl:import href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/BimHouseFunctions.xsl"/>
	<xsl:output method="xml" version="1.0" encoding="UTF-8" indent="yes"/>

	<!--<xsl:template match="*[starts-with(@xsi:type,'ct:Тип.Базовый.Персона')]" mode="presenting">-->
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.Базовый.Персона')]" mode="presenting">
		<xsl:element name="{name()}" namespace="{namespace-uri()}">
			<xsl:copy-of select="@*"/>
			<xsl:apply-templates select="./node()" mode="#current"/>
			<xsl:element name="Представления" namespace="urn:BimHouse:CommonDataType">
				<xsl:element name="ct:ФиоДолжностьПриказРеквизитыОрганизации" namespace="urn:BimHouse:CommonDataType">
					<xsl:call-template name="CreatePersonTextpresentingType1">
						<xsl:with-param name="Person" select="."/>
					</xsl:call-template>
				</xsl:element>
				<xsl:element name="ct:ФиоДолжностьПриказ" namespace="urn:BimHouse:CommonDataType">
					<xsl:call-template name="CreatePersonTextpresentingType2">
						<xsl:with-param name="Person" select="."/>
					</xsl:call-template>
				</xsl:element>
				<xsl:element name="ct:ФиоДолжность" namespace="urn:BimHouse:CommonDataType">
					<xsl:call-template name="CreatePersonTextpresentingType3">
						<xsl:with-param name="Person" select="."/>
					</xsl:call-template>
				</xsl:element>
			</xsl:element>
		</xsl:element>
	</xsl:template>

	<xsl:template name="CreatePersonTextpresentingType1">
		<xsl:param name="Person"/>
		<xsl:choose>
			<xsl:when test="$Person/ct:ФИО/ct:Фамилия != ''">
				<xsl:if test="$Person/ct:ПорядковыйНомер">
					<xsl:value-of select="$Person/ct:ПорядковыйНомер"/>
					<xsl:text>. </xsl:text>
				</xsl:if>
				<xsl:if test="$Person/ct:Должность != ''">
					<xsl:value-of select="$Person/ct:Должность"/>
				</xsl:if>
				<xsl:if test="$Person/ct:ФИО/ct:Фамилия != ''">
					<xsl:text> </xsl:text>
					<xsl:value-of select="$Person/ct:ФИО/ct:Фамилия"/>
					<xsl:text>&#160;</xsl:text>
					<xsl:value-of select="$Person/ct:ФИО/ct:Инициалы"/>
				</xsl:if>
				<xsl:if test="$Person/ct:Организация/ct:Наименование/ct:Краткое != ''">
					<xsl:text>, </xsl:text>
					<xsl:value-of select="$Person/ct:Организация/ct:Наименование/ct:Краткое"/>
				</xsl:if>
				<xsl:if test="$Person/ct:Организация/ct:ОГРН != ''">
					<xsl:text>, ОГРН&#160;</xsl:text>
					<xsl:value-of select="$Person/ct:Организация/ct:ОГРН"/>
				</xsl:if>
				<xsl:choose>
					<xsl:when test="$Person/ct:Организация/ct:ИНН != '' and $Person/ct:Организация/ct:КПП != ''">
						<xsl:text>, ИНН/КПП&#160;</xsl:text>
						<xsl:value-of select="$Person/ct:Организация/ct:ИНН"/>
						<xsl:text>/</xsl:text>
						<xsl:value-of select="$Person/ct:Организация/ct:КПП"/>
					</xsl:when>
					<xsl:when test="$Person/ct:Организация/ct:ИНН != ''">
						<xsl:text>, ИНН&#160;</xsl:text>
						<xsl:value-of select="$Person/ct:Организация/ct:ИНН"/>
					</xsl:when>
				</xsl:choose>
				<xsl:if test="$Person/ct:Организация/ct:АдресЮридический/ct:ТекстовоеПредставление[1] != ''">
					<xsl:text>, </xsl:text>
					<xsl:value-of select="$Person/ct:Организация/ct:АдресЮридический/ct:ТекстовоеПредставление[1]"/>
				</xsl:if>
				<xsl:if test="$Person/ct:Приказ/ct:ТипДокумента and $Person/ct:Приказ/ct:НомерДокумента">
					<xsl:text>, </xsl:text>
					<xsl:value-of select="$Person/ct:Приказ/ct:ТипДокумента"/>
					<xsl:text> №&#160;</xsl:text>
					<xsl:value-of select="$Person/ct:Приказ/ct:НомерДокумента"/>
					<xsl:if test="$Person/ct:Приказ/ct:ДатаДокумента">
						<xsl:text> от </xsl:text>
						<xsl:value-of select="format-date($Person/ct:Приказ/ct:ДатаДокумента, '[D01].[M01].[Y0001]')"/>
					</xsl:if>
				</xsl:if>
				<xsl:for-each select="$Person/ct:ДополнительнаяХарактеристика">
					<xsl:if test="ct:Характеристика != '' and ct:Значение != ''">
						<xsl:text>, </xsl:text>
						<xsl:value-of select="ct:Характеристика"/>
						<xsl:text> </xsl:text>
						<xsl:value-of select="ct:Значение"/>
					</xsl:if>
				</xsl:for-each>
			</xsl:when>
			<xsl:otherwise>
				<xsl:text> </xsl:text>
			</xsl:otherwise>
		</xsl:choose>
	</xsl:template>

	<xsl:template name="CreatePersonTextpresentingType2">
		<xsl:param name="Person"/>
		<xsl:choose>
			<xsl:when test="$Person/ct:ФИО/ct:Фамилия != ''">
				<xsl:if test="$Person/ct:ПорядковыйНомер">
					<xsl:value-of select="$Person/ct:ПорядковыйНомер"/>
					<xsl:text>. </xsl:text>
				</xsl:if>
				<xsl:if test="$Person/ct:Должность != ''">
					<xsl:value-of select="$Person/ct:Должность"/>
				</xsl:if>
				<xsl:if test="$Person/ct:ФИО/ct:Фамилия != ''">
					<xsl:text> </xsl:text>
					<xsl:value-of select="$Person/ct:ФИО/ct:Фамилия"/>
					<xsl:text>&#160;</xsl:text>
					<xsl:value-of select="$Person/ct:ФИО/ct:Инициалы"/>
				</xsl:if>
				<xsl:if test="$Person/ct:Организация/ct:Наименование/ct:Краткое != ''">
					<xsl:text>, </xsl:text>
					<xsl:value-of select="$Person/ct:Организация/ct:Наименование/ct:Краткое"/>
				</xsl:if>
				<xsl:if test="$Person/ct:Приказ/ct:ТипДокумента and $Person/ct:Приказ/ct:НомерДокумента">
					<xsl:text>, </xsl:text>
					<xsl:value-of select="$Person/ct:Приказ/ct:ТипДокумента"/>
					<xsl:text> №&#160;</xsl:text>
					<xsl:value-of select="$Person/ct:Приказ/ct:НомерДокумента"/>
					<xsl:if test="$Person/ct:Приказ/ct:ДатаДокумента">
						<xsl:text> от </xsl:text>
						<xsl:value-of select="format-date($Person/ct:Приказ/ct:ДатаДокумента,'[D01].[M01].[Y0001]')"/>
					</xsl:if>
				</xsl:if>
			</xsl:when>
			<xsl:otherwise>
				<xsl:text> </xsl:text>
			</xsl:otherwise>
		</xsl:choose>
	</xsl:template>

	<xsl:template name="CreatePersonTextpresentingType3">
		<xsl:param name="Person"/>
		<xsl:choose>
			<xsl:when test="$Person/ct:ФИО/ct:Фамилия != ''">
				<xsl:if test="$Person/ct:Должность != ''">
					<xsl:value-of select="$Person/ct:Должность"/>
				</xsl:if>
				<xsl:if test="$Person/ct:Организация/ct:Наименование/ct:Краткое != ''">
					<xsl:text>, </xsl:text>
					<xsl:value-of select="$Person/ct:Организация/ct:Наименование/ct:Краткое"/>
				</xsl:if>
				<xsl:if test="$Person/ct:ФИО/ct:Фамилия != ''">
					<xsl:text> </xsl:text>
					<xsl:value-of select="$Person/ct:ФИО/ct:Фамилия"/>
					<xsl:text>&#160;</xsl:text>
					<xsl:value-of select="$Person/ct:ФИО/ct:Инициалы"/>
				</xsl:if>
			</xsl:when>
			<xsl:otherwise>
				<xsl:text> </xsl:text>
			</xsl:otherwise>
		</xsl:choose>
	</xsl:template>

</xsl:stylesheet>